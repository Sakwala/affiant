namespace Affiant.AgentFramework.Tests.Filters;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.AgentFramework.Filters;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Area-3 P2 ruling 1 contract test: injects the SAME completion-stage-filter failure through
/// both adapters' REAL bridges (<see cref="AffiantAutoFunctionInvocationBridge"/> for SK,
/// <see cref="AffiantFunctionInvocationMiddleware"/> for MAF) and asserts the model-visible
/// payload is equivalent on both.
///
/// <para>
/// This lives in <c>Affiant.AgentFramework.Tests</c> rather than either single-adapter test
/// project because it is the one test project already permitted to reference both adapter
/// packages at once (see this project's own <c>.csproj</c> comment, established for the
/// tool-catalog reflection parity test).
/// </para>
///
/// <para>
/// <b>Why the assertion is "the genuine tool result is preserved," not "both produce a
/// ToolError":</b> ruling 1 ("an exception thrown by ANY completion-stage filter must reach the
/// model... never propagate raw") and ruling 3 ("an extractor/post-tool-filter exception NEVER
/// reports tool failure to the model — surface-and-continue") combine into one contract, not two
/// competing ones: on BOTH adapters, <c>ToolInvocationContext.ToolExecuted</c> is already
/// <see langword="true"/> by the time a completion-stage filter's own logic can throw (the real
/// tool call already succeeded and set it), so <c>ToolErrorFilter</c>'s ToolExecuted-gated catch
/// (present in both the SK completion-stage onion, via
/// <c>Affiant.SemanticKernel.Filters.BridgeStages.CompletionStage</c>, and MAF's one onion)
/// classifies the injected failure as post-processing, not a tool failure — never converting it to
/// a <see cref="ToolError"/>, never discarding the tool's real output, never retrying (which would
/// re-execute the tool). "The same observable shape" is therefore: the model sees the tool's
/// genuine result on both backends, unchanged, and the injected failure is visible only via the
/// <c>affiant.extractor.failed</c> OTel event — asserted below on both adapters too.
/// </para>
/// </summary>
public class CrossAdapterCompletionStageFailureContractTests
{
    private const string GenuineToolResult = "genuine-tool-result";
    private const string InjectedFailureMessage = "boom-completion-stage-filter";

    // Named by literal, not AffiantTelemetry.AffiantActivitySource.Name: referencing that static
    // field from inside ShouldListenTo can run the type's static constructor from within
    // ActivitySource's own constructor call graph (AddActivityListener notifies existing listeners
    // synchronously as new sources are created) — a re-entrant race that throws
    // NullReferenceException the first time any test in a filtered/isolated run touches
    // AffiantTelemetry. The literal is the same "Affiant.Framework" name asserted by
    // ToolErrorTelemetryTests elsewhere.
    private static ActivityListener FrameworkListener() => new()
    {
        ShouldListenTo = source => source.Name == "Affiant.Framework",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    [Fact]
    public async Task InjectedCompletionStageFailure_SK_PreservesGenuineResult_NeverPropagatesRaw()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);
        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var services = new ServiceCollection();
        // Same relative registration order as production: ToolErrorFilter (ruling 2, outermost)
        // before the completion-stage filter under test.
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        services.AddSingleton<IToolInvocationFilter>(new ThrowingCompletionStageFilter());
        var sp = services.BuildServiceProvider();

        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        var kernel = new Kernel(sp);
        var function = KernelFunctionFactory.CreateFromMethod(() => "unused", "DoTool");
        var initialResult = new FunctionResult(function, GenuineToolResult);
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, "calling DoTool");
        var context = new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage);

        // Simulates SK's own remaining auto-invocation chain — a no-op, since context.Result
        // already carries the tool's (already-executed) genuine output (see
        // AffiantAutoFunctionInvocationBridgeReviewGateTests for the same pattern).
        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var resultText = context.Result.GetValue<object>() as string;
        Assert.Equal(GenuineToolResult, resultText); // never discarded, never converted to ToolError

        var evt = Assert.Single(span!.Events, e => e.Name == "affiant.extractor.failed");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("DoTool", tags["tool.name"]);
        Assert.Equal("InvalidOperationException", tags["exception.type"]);
    }

    [Fact]
    public async Task InjectedCompletionStageFailure_MAF_PreservesGenuineResult_NeverPropagatesRaw()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);
        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var services = new ServiceCollection();
        // Same relative registration order as production and as the SK test above.
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        services.AddSingleton<IToolInvocationFilter>(new ThrowingCompletionStageFilter());
        var sp = services.BuildServiceProvider();

        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var middleware = new AffiantFunctionInvocationMiddleware(pipeline, new StubRegistry());

        var function = AIFunctionFactory.Create(() => GenuineToolResult, name: "DoTool");
        var context = new Microsoft.Extensions.AI.FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(),
            Messages = new List<ChatMessage>(),
        };
        var stubAgent = new ChatClientAgent(new NoOpChatClient(), instructions: "stub");

        var result = await middleware.InvokeAsync(
            stubAgent, context, (_, _) => new ValueTask<object?>(GenuineToolResult), CancellationToken.None);

        Assert.Equal(GenuineToolResult, result); // never discarded, never converted to ToolError

        var evt = Assert.Single(span!.Events, e => e.Name == "affiant.extractor.failed");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("DoTool", tags["tool.name"]);
        Assert.Equal("InvalidOperationException", tags["exception.type"]);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>Injected completion-stage failure: runs the tool, then always throws.</summary>
    private sealed class ThrowingCompletionStageFilter : ICompletionStageFilter
    {
        public async Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            await next(context); // the tool (or the rest of the chain) runs and succeeds first
            throw new InvalidOperationException(InjectedFailureMessage);
        }
    }

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        public void Register(AffiantToolDescriptor descriptor) { }
        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) => null;
        public IReadOnlyList<AffiantToolDescriptor> All => [];
    }
}
