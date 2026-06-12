namespace Affiant.SemanticKernel.Tests.Integration;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

using static IntegrationTestPipelineFactory;

/// <summary>
/// Integration tests closing L2 AC #14: verifies that InferenceTriggerFilter runs inference
/// at most once per (ConversationId, FunctionName, TurnNumber) tuple.
///
/// The idempotency bookkeeping lives in IContextFabric (singleton) under the reserved key
/// "inference_idempotency". Each (convId|funcName|turnNumber) key is marked seen after the
/// first trigger; subsequent invocations with the same key skip inference.
///
/// Tests also verify that registering multiple IInferenceTrigger instances all returning true
/// does NOT cause the port to be called multiple times (short-circuit + idempotency cooperate).
/// </summary>
[Collection("AffiantL2IntegrationTests")]
public class InferenceIdempotencyIntegrationTests
{
    [Fact]
    public async Task TwoTriggersBothReturnTrue_InferenceRunsExactlyOnce()
    {
        var portInvocations = 0;
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => { Interlocked.Increment(ref portInvocations); return Task.FromResult(SampleInferenceJson); },
            additionalTriggers: [new AlwaysTrueTrigger(), new AlwaysTrueTrigger()]);

        var result = await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        Assert.NotNull(result);

        // Short-circuit on the first true trigger (WriteIntentInferenceTrigger) prevents
        // the AlwaysTrueTrigger instances from running at all. Idempotency bookkeeping then
        // prevents any duplicate inference within the same (conv, fn, turn) tuple.
        Assert.Equal(1, portInvocations);

        var events = exporter.ExportedActivities.SelectMany(a => a.Events).ToList();
        Assert.Single(events, (System.Diagnostics.ActivityEvent e) => e.Name == "inference.triggered");
        Assert.Single(events, (System.Diagnostics.ActivityEvent e) => e.Name == "inference.completed");
    }

    [Fact]
    public async Task SameFunction_TwoTurns_RunsInferenceTwice()
    {
        var portInvocations = 0;
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => { Interlocked.Increment(ref portInvocations); return Task.FromResult(SampleInferenceJson); });

        // Turn 0: idempotency key (test-conv-001|CreateThing|0) — first invocation
        kernel.Data["AffiantTurnNumber"] = 0;
        await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        // Turn 1: idempotency key (test-conv-001|CreateThing|1) — different key → inference runs again
        kernel.Data["AffiantTurnNumber"] = 1;
        await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        Assert.Equal(2, portInvocations);
        var events = exporter.ExportedActivities.SelectMany(a => a.Events).ToList();
        Assert.Equal(2, events.Count(e => e.Name == "inference.completed"));
    }

    [Fact]
    public async Task SameTurn_DifferentFunctions_BothRunInference()
    {
        // Two write tools, same turn: idempotency keys differ by function name
        //   (test-conv-001|CreateThing|0) vs (test-conv-001|CreateOtherThing|0)
        // so inference runs once per tool → port called twice total.
        var portInvocations = 0;
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => { Interlocked.Increment(ref portInvocations); return Task.FromResult(SampleInferenceJson); },
            registerSecondTool: true);

        kernel.Data["AffiantTurnNumber"] = 0;
        await kernel.InvokeAsync("ThingPlugin", "CreateThing");
        await kernel.InvokeAsync("ThingPlugin", "CreateOtherThing");

        Assert.Equal(2, portInvocations);
    }

    private sealed class AlwaysTrueTrigger : IInferenceTrigger
    {
        public bool ShouldRun(InferenceTriggerContext context) => true;
    }
}
