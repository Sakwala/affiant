namespace Affiant.Extensions.AI.Tests.Filters;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Filters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The third arm of the cross-adapter completion-stage failure contract (area-3 P2 ruling 1 + ruling
/// 3). The Semantic Kernel and Microsoft Agent Framework arms live together in
/// <c>tests/Affiant.AgentFramework.Tests/Filters/CrossAdapterCompletionStageFailureContractTests.cs</c>
/// — the one test project permitted to reference both of those adapter packages at once. This file
/// injects the same failure, with the same expectations and the same literals, through
/// <see cref="AffiantDelegatingAIFunction"/>, so all three shipped seams are held to one contract.
///
/// <para>
/// <b>The contract, and why the assertion is "the genuine tool result survives" rather than "a
/// ToolError is produced":</b> ruling 1 ("an exception thrown by ANY completion-stage filter must
/// reach the model... never propagate raw") and ruling 3 ("an extractor/post-tool-filter exception
/// NEVER reports tool failure to the model — surface-and-continue") are one contract, not two
/// competing ones. On every adapter, <see cref="ToolInvocationContext.ToolExecuted"/> is already
/// <see langword="true"/> by the time a completion-stage filter's own logic can throw — the real tool
/// call already succeeded and set it — so <see cref="ToolErrorFilter"/>'s ToolExecuted-gated catch
/// classifies the failure as post-processing, never converting it to a <see cref="ToolError"/>, never
/// discarding the tool's output, never retrying (which would re-execute the tool). The injected
/// failure is visible only as an <c>affiant.extractor.failed</c> OTel event.
/// </para>
///
/// <para>
/// This arm is the sharpest available test of whether the wrapped-<see cref="AIFunction"/> seam
/// genuinely supports post-invocation result inspection rather than being a fire-and-forget callback
/// — the adapter-contract census (<c>research/langchain-adapter-contract.md</c> §3.2) names exactly
/// this test as the one a third backend must reproduce.
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
    // AffiantTelemetry. Same reasoning, same literal, as the SK/MAF arms.
    private static ActivityListener FrameworkListener() => new()
    {
        ShouldListenTo = source => source.Name == "Affiant.Framework",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    [Fact]
    public async Task InjectedCompletionStageFailure_ExtensionsAI_PreservesGenuineResult_NeverPropagatesRaw()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);
        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var services = new ServiceCollection();
        // Same relative registration order as production and as the SK/MAF arms: ToolErrorFilter
        // (ruling 2, outermost) before the completion-stage filter under test.
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        services.AddSingleton<IToolInvocationFilter>(new ThrowingCompletionStageFilter());
        var sp = services.BuildServiceProvider();

        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var wrapped = new AffiantDelegatingAIFunction(
            AIFunctionFactory.Create(() => GenuineToolResult, name: "DoTool"),
            pipeline,
            new StubRegistry());

        var result = await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        // never discarded, never converted to ToolError. Compared as text rather than by equality:
        // unlike the MAF arm — whose test hands the middleware a raw string as the tool's return
        // value — a real AIFunction's result arrives here as the JsonElement AIFunctionFactory
        // serialized it into, which is the shape every filter downstream reads too (this is why
        // ReviewGateFilter probes `context.Result as string ?? context.Result?.ToString()`).
        Assert.Equal(GenuineToolResult, result?.ToString()?.Trim('"'));

        var evt = Assert.Single(span!.Events, e => e.Name == "affiant.extractor.failed");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("DoTool", tags["tool.name"]);
        Assert.Equal("InvalidOperationException", tags["exception.type"]);
    }

    /// <summary>
    /// The negative control the SK/MAF arms leave implicit and this seam needs explicitly: with no
    /// completion-stage filter throwing, the same wiring produces the same result and emits no
    /// failure event at all. Without it, an implementation that always returned the tool's own value
    /// and always emitted the event would satisfy the test above.
    /// </summary>
    [Fact]
    public async Task NoCompletionStageFailure_SameResult_AndNoExtractorFailedEvent()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);
        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        var sp = services.BuildServiceProvider();

        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var wrapped = new AffiantDelegatingAIFunction(
            AIFunctionFactory.Create(() => GenuineToolResult, name: "DoTool"),
            pipeline,
            new StubRegistry());

        var result = await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        Assert.Equal(GenuineToolResult, result?.ToString()?.Trim('"'));
        Assert.DoesNotContain(span!.Events, e => e.Name == "affiant.extractor.failed");
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
