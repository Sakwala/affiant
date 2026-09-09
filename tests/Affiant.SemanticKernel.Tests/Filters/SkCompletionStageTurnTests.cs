namespace Affiant.SemanticKernel.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Connectors;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// PV-3 grades a field <c>Conversation</c> when the value is literally present in the turn, and the
/// framework establishes that from the turn itself — so every seam that reaches
/// <see cref="TaskInferenceMergeFilter"/> has to hand the turn over. On Semantic Kernel the
/// completion stage runs in its own <c>pipeline.RunAsync</c> call, and these two entry points build
/// its request: the auto-invocation bridge and the manual invoker. Both went through this seam
/// without a history, so the merge filter received no turn and fell back to the port's own claim
/// (design record A16). These are the tests that the real bridge — not a hand-built context —
/// carries it.
/// </summary>
public class SkCompletionStageTurnTests
{
    private sealed class PriorityStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";

        public IReadOnlyList<TaskInferenceField> Fields { get; } =
            [new("Priority", "string", "How urgent")];

        public double? MinimumConfidenceThreshold => null;
    }

    private const string ToolResult = """{"Priority":{"value":"Critical","confidence":0.9}}""";

    private const string Utterance = "Priority Critical, please";

    /// <summary>
    /// The framework's own completion-stage wiring, with the one filter this rule is about and
    /// nothing else: the fabric the step writes to is resolvable, so the test reads the chain the
    /// real filter minted.
    /// </summary>
    private static (ServiceProvider Services, ContextFabric Fabric, ToolInvocationPipeline Pipeline) Build()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor(
            "CreateThing", "ThingPlugin", new Operation("WriteCreate"), "Thing", typeof(PriorityStrategy)));

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(fabric)
            .AddSingleton<PriorityStrategy>()
            .AddSingleton<IAffiantToolRegistry>(registry)
            .AddSingleton(sp => new TaskInferenceStep(
                sp.GetRequiredService<ContextFabric>(), NullLogger<TaskInferenceStep>.Instance))
            .AddSingleton<IToolInvocationFilter>(sp => new TaskInferenceMergeFilter(
                sp.GetRequiredService<TaskInferenceStep>(),
                sp.GetRequiredService<IAffiantToolRegistry>(),
                NullLogger<TaskInferenceMergeFilter>.Instance))
            .BuildServiceProvider();

        return (services, fabric, new ToolInvocationPipeline(services.GetRequiredService<IServiceScopeFactory>()));
    }

    /// <summary>The history a host puts on the kernel, which is where both bridges read it from.</summary>
    private static Kernel KernelWithTurn(IServiceProvider services, string? utterance)
    {
        var kernel = new Kernel(services);
        if (utterance is not null)
        {
            var history = new ChatHistory();
            history.AddUserMessage(utterance);
            kernel.Data["ChatHistory"] = history;
        }

        return kernel;
    }

    /// <summary>
    /// A16: the completion-stage bridge builds the request, so the request carries the turn. The
    /// value the port reported is in what the person typed, so it is graded from the text and bound
    /// to the place it was read from — the grade this seam could not reach before.
    /// </summary>
    [Fact]
    public async Task TheAutoInvocationBridge_CarriesTheTurn_SoAValueInItIsConversation()
    {
        var (services, fabric, pipeline) = Build();
        using var _ = services;
        var kernel = KernelWithTurn(services, Utterance);
        var function = KernelFunctionFactory.CreateFromMethod(() => ToolResult, "CreateThing", "ThingPlugin");
        var context = new AutoFunctionInvocationContext(
            kernel,
            function,
            new FunctionResult(function, ToolResult),
            new ChatHistory(),
            new ChatMessageContent(AuthorRole.Assistant, "calling CreateThing"));

        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);
        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var tag = fabric.GetFieldChain("Priority")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        var span = Assert.IsType<ProvenanceBinding.UtteranceSpan>(tag.Binding).Ref;
        Assert.Equal(9, span.Offset);
        Assert.Equal("Critical", Utterance.Substring(span.Offset, span.Length));
    }

    /// <summary>
    /// The control: neither reading has a turn — no history on the kernel and an empty
    /// <see cref="AutoFunctionInvocationContext.ChatHistory"/> — and the step says so rather than
    /// inventing one. Without the fix above every SK turn looked like this one.
    /// </summary>
    [Fact]
    public async Task TheAutoInvocationBridge_WithNeitherHistory_HasNoTurn()
    {
        var (services, fabric, pipeline) = Build();
        using var _ = services;
        var kernel = KernelWithTurn(services, utterance: null);
        var function = KernelFunctionFactory.CreateFromMethod(() => ToolResult, "CreateThing", "ThingPlugin");
        var context = new AutoFunctionInvocationContext(
            kernel,
            function,
            new FunctionResult(function, ToolResult),
            new ChatHistory(),
            new ChatMessageContent(AuthorRole.Assistant, "calling CreateThing"));

        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);
        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var tag = fabric.GetFieldChain("Priority")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>
    /// A25: the kernel convention is a host contract nothing in the framework populates, so a host
    /// that never adopted it used to reach the merge filter with no turn — #123 unfixed for that
    /// host. Semantic Kernel hands the bridge its own <c>ChatHistory</c> on every auto-invocation
    /// call; with nothing under <c>kernel.Data["ChatHistory"]</c> the bridge reads that instead, and
    /// the value the person typed is graded from the text and bound to where it was read.
    /// </summary>
    [Fact]
    public async Task TheAutoInvocationBridge_WithOnlySksOwnHistory_IsConversation()
    {
        var (services, fabric, pipeline) = Build();
        using var _ = services;
        var kernel = KernelWithTurn(services, utterance: null);
        Assert.False(kernel.Data.ContainsKey("ChatHistory"));
        var skHistory = new ChatHistory();
        skHistory.AddUserMessage(Utterance);
        var function = KernelFunctionFactory.CreateFromMethod(() => ToolResult, "CreateThing", "ThingPlugin");
        var context = new AutoFunctionInvocationContext(
            kernel,
            function,
            new FunctionResult(function, ToolResult),
            skHistory,
            new ChatMessageContent(AuthorRole.Assistant, "calling CreateThing"));

        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);
        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var tag = fabric.GetFieldChain("Priority")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        var span = Assert.IsType<ProvenanceBinding.UtteranceSpan>(tag.Binding).Ref;
        Assert.Equal(9, span.Offset);
        Assert.Equal("Critical", Utterance.Substring(span.Offset, span.Length));
    }

    /// <summary>
    /// A25, the half a "non-empty history wins" reading would have missed: a host that puts a
    /// system-prompt-only <c>ChatHistory</c> on the kernel has adopted the convention badly rather
    /// than not at all. That history holds no turn of a person's, so the bridge asks whether the
    /// kernel's reading carries a user message rather than whether it is non-empty, and falls back
    /// to the history Semantic Kernel hands it — which does carry the turn.
    /// </summary>
    [Fact]
    public async Task TheAutoInvocationBridge_WithASystemOnlyKernelHistory_ReadsSksOwn()
    {
        var (services, fabric, pipeline) = Build();
        using var _ = services;
        var kernel = new Kernel(services);
        kernel.Data["ChatHistory"] = new ChatHistory("You are a maintenance assistant");
        var skHistory = new ChatHistory();
        skHistory.AddUserMessage(Utterance);
        var function = KernelFunctionFactory.CreateFromMethod(() => ToolResult, "CreateThing", "ThingPlugin");
        var context = new AutoFunctionInvocationContext(
            kernel,
            function,
            new FunctionResult(function, ToolResult),
            skHistory,
            new ChatMessageContent(AuthorRole.Assistant, "calling CreateThing"));

        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);
        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        var tag = fabric.GetFieldChain("Priority")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        var span = Assert.IsType<ProvenanceBinding.UtteranceSpan>(tag.Binding).Ref;
        Assert.Equal(9, span.Offset);
        Assert.Equal("Critical", Utterance.Substring(span.Offset, span.Length));
    }

    /// <summary>
    /// A16: the degraded path — the manual invoker a provider without SK's auto-invocation loop
    /// takes — runs the same completion stage and now reads the same history, so the same tool
    /// result on the same turn grades the same way on both paths.
    /// </summary>
    [Fact]
    public async Task TheManualInvoker_CarriesTheTurn_SoAValueInItIsConversation()
    {
        var (services, fabric, pipeline) = Build();
        using var _ = services;
        var kernel = KernelWithTurn(services, Utterance);
        kernel.Plugins.AddFromFunctions(
            "ThingPlugin", [KernelFunctionFactory.CreateFromMethod(() => ToolResult, "CreateThing")]);

        var invoker = new ManualToolInvoker(pipeline, NullLogger<ManualToolInvoker>.Instance);
        await invoker.CaptureAndInvokeAsync(
            new FunctionCallContent("CreateThing", "ThingPlugin", "call-1"), kernel, CancellationToken.None);

        var tag = fabric.GetFieldChain("Priority")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(9, Assert.IsType<ProvenanceBinding.UtteranceSpan>(tag.Binding).Ref.Offset);
    }
}
