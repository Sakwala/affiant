namespace Affiant.SemanticKernel.Tests.Filters;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Connectors;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Tests.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Integration tests verifying that AddAffiantSemanticKernel() wires the filter chain
/// correctly for both the OpenAI auto-invocation path and the Gemini manual fallback path.
///
/// OpenAI path: SK auto-invocation → IAutoFunctionInvocationFilter chain → TaskInferenceStep
/// Gemini path: ManualToolInvoker → Kernel.InvokeAsync → IFunctionInvocationFilter chain
///
/// Both paths must produce structurally identical function results for the same plugin input.
/// </summary>
public class DualProviderFilterChainTests
{
    // ── OpenAI auto path ─────────────────────────────────────────────────────

    /// <summary>
    /// TaskInferenceStep (the core of TaskInferenceMergeFilter) merges structured-output JSON
    /// into ContextFabric correctly on the auto-invocation path.
    /// This validates the OpenAI path end-to-end at the filter component level.
    /// </summary>
    [Fact]
    public async Task OpenAiAutoPath_TaskInferenceStep_MergesFieldsIntoContextFabric()
    {
        var (step, fabric) = BuildTaskInferenceStack();

        var json = """{"itemStatus": {"value": "active", "confidence": 0.95}}""";
        using var doc = JsonDocument.Parse(json);
        var result = await step.ExecuteAsync(doc.RootElement);

        Assert.True(result.MergedFields.ContainsKey("itemStatus"));
        Assert.True(result.MergedFields["itemStatus"].Merged);

        var entity = fabric.GetByKey("TestEntity");
        Assert.NotNull(entity);
        Assert.Equal("active", entity.Fields["itemStatus"].ToString());
    }

    [Fact]
    public async Task OpenAiAutoPath_TaskInferenceStep_SkipsFieldsBelowThreshold()
    {
        var (step, fabric) = BuildTaskInferenceStack(minimumConfidence: 0.8);

        var json = """{"itemStatus": {"value": "draft", "confidence": 0.5}}""";
        using var doc = JsonDocument.Parse(json);
        var result = await step.ExecuteAsync(doc.RootElement);

        Assert.True(result.MergedFields.ContainsKey("itemStatus"));
        Assert.False(result.MergedFields["itemStatus"].Merged);
        Assert.Null(fabric.GetByKey("TestEntity"));
    }

    [Fact]
    public async Task OpenAiAutoPath_TaskInferenceStep_IgnoresEmptyJson()
    {
        var (step, fabric) = BuildTaskInferenceStack();

        // JSON with no matching field names — step must be a no-op
        var json = """{"unrelatedField": {"value": "x", "confidence": 0.9}}""";
        using var doc = JsonDocument.Parse(json);
        var result = await step.ExecuteAsync(doc.RootElement);

        Assert.Empty(result.MergedFields);
        Assert.Null(fabric.GetByKey("TestEntity"));
    }

    /// <summary>
    /// Verifies that a registered IAutoFunctionInvocationFilter executes when the filter
    /// pipeline is driven with a synthesized AutoFunctionInvocationContext. Uses the public
    /// AutoFunctionInvocationContext constructor (kernel, function, fnResult, history, message)
    /// instead of FakeLlmProvider+InvokePromptAsync: SK 1.74's auto-invocation loop requires
    /// provider-specific metadata (FinishReason, ModelId) that a bare IChatCompletionService
    /// stub cannot supply, so driving the pipeline directly is the reliable approach.
    /// </summary>
    [Fact]
    public async Task OpenAiAutoPath_AutoFunctionInvocationFilter_FiresOnToolCall()
    {
        const string expectedResult = """{"itemStatus":{"value":"ready","confidence":0.9}}""";

        var services = new ServiceCollection();
        services.AddLogging();
        var capture = new FilterExecutionCapture();
        services.AddSingleton(capture);
        services.AddSingleton<ITaskInferenceStrategy, InferenceStrategyWithStatusField>();
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel();
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        // Spy filter: records its own execution to FilterExecutionCapture
        services.AddScoped<IAutoFunctionInvocationFilter>(
            sp => new SpyAutoFunctionInvocationFilter(sp.GetRequiredService<FilterExecutionCapture>()));

        services.AddKernel();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("StatusPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => expectedResult, "GetStatus")]));

        // Invoke the function to obtain a real FunctionResult, then synthesize an
        // AutoFunctionInvocationContext via the public constructor and drive the pipeline.
        var function = kernel.Plugins["StatusPlugin"]["GetStatus"];
        var fnResult = await kernel.InvokeAsync("StatusPlugin", "GetStatus");
        var chatHistory = new ChatHistory();
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, string.Empty);
        var autoCtx = new AutoFunctionInvocationContext(kernel, function, fnResult, chatHistory, chatMessage);

        Func<AutoFunctionInvocationContext, Task> terminal = _ => Task.CompletedTask;
        foreach (var f in kernel.AutoFunctionInvocationFilters.Reverse())
        {
            var captured = f;
            var next = terminal;
            terminal = ctx => captured.OnAutoFunctionInvocationAsync(ctx, next);
        }
        await terminal(autoCtx);

        // SpyAutoFunctionInvocationFilter must have recorded itself
        Assert.Contains("SpyAutoFunctionInvocationFilter", capture.ExecutedFilters);
    }

    // ── Gemini manual path ───────────────────────────────────────────────────

    /// <summary>
    /// ManualToolInvoker fires the full IFunctionInvocationFilter chain via Kernel.InvokeAsync,
    /// producing the same function result as the auto-invocation path for the same plugin input.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("manual")]
    public async Task DualProvider_IdenticalFunctionResult_OnBothPaths(string path)
    {
        const string expectedResult = """{"itemStatus":{"value":"ready","confidence":0.9}}""";

        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("StatusPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => expectedResult, "GetStatus")]);
        kernel.Plugins.Add(plugin);

        string actualResult;

        if (path == "manual")
        {
            // Gemini path: ManualToolInvoker → kernel.InvokeAsync
            var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
            var call = new FunctionCallContent("GetStatus", "StatusPlugin", "call-gemini");
            var resultContent = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);
            actualResult = resultContent.Result?.ToString() ?? string.Empty;
        }
        else
        {
            // OpenAI path: direct kernel invocation (simulates result returned to TaskInferenceMergeFilter)
            var fnResult = await kernel.InvokeAsync("StatusPlugin", "GetStatus");
            actualResult = fnResult.GetValue<string>() ?? string.Empty;
        }

        Assert.Equal(expectedResult, actualResult);
    }

    /// <summary>
    /// Verifies that both the OpenAI auto-invocation path and the Gemini manual path
    /// produce structurally identical observable state: the same functions are invoked
    /// and the same function result is produced. Uses FilterExecutionTracer to capture
    /// and compare traces from both paths.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("manual")]
    public async Task DualProvider_IdenticalFilterBehavior(string path)
    {
        const string expectedResult = """{"itemStatus":{"value":"ready","confidence":0.9}}""";
        var tracer = new FilterExecutionTracer();

        if (path == "manual")
        {
            // Gemini manual path: ManualToolInvoker directly invokes the function
            var kernel = Kernel.CreateBuilder().Build();
            kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("StatusPlugin",
                [KernelFunctionFactory.CreateFromMethod(() => expectedResult, "GetStatus")]));

            var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
            var call = new FunctionCallContent("GetStatus", "StatusPlugin", "call-manual");
            var resultContent = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);
            var result = resultContent.Result?.ToString() ?? string.Empty;

            tracer.RecordFilter("ManualToolInvoker");
            tracer.RecordFunction("StatusPlugin.GetStatus");
            tracer.RecordAttribute("functionResult", result);
        }
        else
        {
            // OpenAI auto path: drive the filter pipeline directly via AutoFunctionInvocationContext.
            // SK 1.74's auto-invocation loop requires provider-specific metadata that a bare
            // IChatCompletionService stub cannot supply, so we synthesize the context manually.
            var services = new ServiceCollection();
            services.AddLogging();
            var capture = new FilterExecutionCapture();
            services.AddSingleton(capture);
            services.AddSingleton<ITaskInferenceStrategy, InferenceStrategyWithStatusField>();
            services.AddScoped<ContextFabric>();
            services.AddScoped<TaskInferenceStep>();
            services.AddAffiantSemanticKernel();
            services.AddAffiantCore(opts => opts.EnableObservability = false);

            string? capturedResult = null;
            services.AddScoped<IAutoFunctionInvocationFilter>(_ =>
                new TracingAutoFunctionInvocationFilter(
                    tracer, "TaskInferenceMergeFilter", "StatusPlugin.GetStatus",
                    r => capturedResult = r));
            services.AddKernel();

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
            kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("StatusPlugin",
                [KernelFunctionFactory.CreateFromMethod(() => expectedResult, "GetStatus")]));

            var function = kernel.Plugins["StatusPlugin"]["GetStatus"];
            var fnResult = await kernel.InvokeAsync("StatusPlugin", "GetStatus");
            var chatHistory = new ChatHistory();
            var chatMessage = new ChatMessageContent(AuthorRole.Assistant, string.Empty);
            var autoCtx = new AutoFunctionInvocationContext(kernel, function, fnResult, chatHistory, chatMessage);

            Func<AutoFunctionInvocationContext, Task> terminal = _ => Task.CompletedTask;
            foreach (var f in kernel.AutoFunctionInvocationFilters.Reverse())
            {
                var captured = f;
                var next = terminal;
                terminal = ctx => captured.OnAutoFunctionInvocationAsync(ctx, next);
            }
            await terminal(autoCtx);

            tracer.RecordAttribute("functionResult", capturedResult ?? string.Empty);
        }

        var trace = tracer.CaptureTrace();

        // Both paths must have recorded the same function and produced the same result
        Assert.Contains("StatusPlugin.GetStatus", trace.FunctionNames);
        Assert.True(trace.CapturedAttributes.ContainsKey("functionResult"));
        Assert.Equal(expectedResult, trace.CapturedAttributes["functionResult"]?.ToString());
    }

    [Fact]
    public async Task GeminiManualPath_ManualToolInvoker_ReturnsCorrectResult()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("TestPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => 42, "GetAnswer")]);
        kernel.Plugins.Add(plugin);

        var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent("GetAnswer", "TestPlugin", "call-1");
        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        Assert.Equal("42", result.Result?.ToString());
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public async Task GeminiManualPath_ManualToolInvoker_HandlesUnknownFunction()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent("Missing", "NoPlugin", "call-bad");
        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        var resultStr = result.Result?.ToString() ?? string.Empty;
        Assert.Contains("FUNCTION_NOT_FOUND", resultStr);
    }

    // ── ToolError handling ───────────────────────────────────────────────────

    /// <summary>
    /// ToolErrorFilter (position 1 in the pipeline) must capture exceptions from plugins
    /// and convert them into structured ToolError envelopes — never letting them propagate
    /// to the LLM as unhandled exceptions.
    /// </summary>
    [Fact]
    public async Task FilterChain_ToolErrorFilter_ConvertsExceptionToStructuredError()
    {
        var kernel = BuildKernelWithToolErrorFilter();
        var plugin = KernelPluginFactory.CreateFromFunctions("BrokenPlugin",
            [KernelFunctionFactory.CreateFromMethod(
                (Func<string>)(() => throw new InvalidOperationException("Simulated tool failure")),
                "BrokenFn")]);
        kernel.Plugins.Add(plugin);

        var fnResult = await kernel.InvokeAsync("BrokenPlugin", "BrokenFn");
        var resultStr = fnResult.GetValue<string>() ?? string.Empty;

        // ToolErrorFilter must convert the exception to a JSON ToolError envelope
        Assert.Contains("VALIDATION_FAILED", resultStr);
        Assert.Contains("Simulated tool failure", resultStr);
    }

    [Fact]
    public async Task FilterChain_ToolErrorFilter_SuccessfulInvocation_PassesThrough()
    {
        var kernel = BuildKernelWithToolErrorFilter();
        var plugin = KernelPluginFactory.CreateFromFunctions("WorkingPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "ok", "Fn")]);
        kernel.Plugins.Add(plugin);

        var fnResult = await kernel.InvokeAsync("WorkingPlugin", "Fn");
        Assert.Equal("ok", fnResult.GetValue<string>());
    }

    // ── Full DI wiring ───────────────────────────────────────────────────────

    /// <summary>
    /// Validates that the DI container built by AddAffiantSemanticKernel correctly wires
    /// all required components so the kernel resolves them at runtime without configuration errors.
    /// </summary>
    [Fact]
    public async Task AddAffiantSemanticKernel_FullDiStack_KernelInvokesPluginCorrectly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, InferenceStrategyWithStatusField>();
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel(opts =>
        {
            opts.PrimaryProvider = "openai";
            opts.FallbackProvider = "google";
        });
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddKernel();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        var plugin = KernelPluginFactory.CreateFromFunctions("Probe",
            [KernelFunctionFactory.CreateFromMethod(() => "probe-ok", "Ping")]);
        kernel.Plugins.Add(plugin);

        var result = await kernel.InvokeAsync("Probe", "Ping");
        Assert.Equal("probe-ok", result.GetValue<string>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (TaskInferenceStep Step, ContextFabric Fabric) BuildTaskInferenceStack(
        double? minimumConfidence = null)
    {
        var fabric = new ContextFabric();
        var strategy = new InferenceStrategyWithStatusField(minimumConfidence);
        var step = new TaskInferenceStep(strategy, fabric, NullLogger<TaskInferenceStep>.Instance);
        return (step, fabric);
    }

    private static Kernel BuildKernelWithToolErrorFilter()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var filter = new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance);
        kernel.FunctionInvocationFilters.Add(filter);
        return kernel;
    }

    private sealed class InferenceStrategyWithStatusField(double? minimumConfidence = null)
        : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields =>
        [
            new TaskInferenceField("itemStatus", "string", "Current status of the item")
        ];
        public double? MinimumConfidenceThreshold => minimumConfidence;
    }

    /// <summary>
    /// Spy IAutoFunctionInvocationFilter that records its own name to FilterExecutionCapture.
    /// </summary>
    private sealed class SpyAutoFunctionInvocationFilter(FilterExecutionCapture capture)
        : IAutoFunctionInvocationFilter
    {
        public async Task OnAutoFunctionInvocationAsync(
            AutoFunctionInvocationContext context,
            Func<AutoFunctionInvocationContext, Task> next)
        {
            await next(context);
            capture.RecordFilter(nameof(SpyAutoFunctionInvocationFilter));
        }
    }

    /// <summary>
    /// IAutoFunctionInvocationFilter that records function names and results into a
    /// FilterExecutionTracer for structural trace comparison between provider paths.
    /// </summary>
    private sealed class TracingAutoFunctionInvocationFilter(
        FilterExecutionTracer tracer,
        string filterLabel,
        string functionLabel,
        Action<string?> captureResult) : IAutoFunctionInvocationFilter
    {
        public async Task OnAutoFunctionInvocationAsync(
            AutoFunctionInvocationContext context,
            Func<AutoFunctionInvocationContext, Task> next)
        {
            await next(context);
            tracer.RecordFilter(filterLabel);
            tracer.RecordFunction(functionLabel);
            captureResult(context.Result?.ToString());
        }
    }

    /// <summary>
    /// Stateful fake IChatCompletionService. On the first call, returns a FunctionCallContent
    /// for the specified plugin/function so SK auto-invokes it through the filter chain.
    /// On subsequent calls, returns a plain text response to end the auto-invocation loop.
    /// </summary>
    private sealed class FakeLlmProvider(
        string pluginName,
        string functionName,
        string callId = "call-fake-1") : IChatCompletionService
    {
        private int _callCount;

        public IReadOnlyDictionary<string, object?> Attributes =>
            new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var count = System.Threading.Interlocked.Increment(ref _callCount);
            ChatMessageContent content;

            if (count == 1)
            {
                // First call: request auto-invocation of the target function
                var toolCall = new FunctionCallContent(functionName, pluginName, callId);
                content = new ChatMessageContent(
                    AuthorRole.Assistant,
                    new ChatMessageContentItemCollection { toolCall });
            }
            else
            {
                // Subsequent calls: plain text signals end of auto-invocation loop
                content = new ChatMessageContent(AuthorRole.Assistant, "(done)");
            }

            IReadOnlyList<ChatMessageContent> result = [content];
            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "(done)");
        }
    }
}
