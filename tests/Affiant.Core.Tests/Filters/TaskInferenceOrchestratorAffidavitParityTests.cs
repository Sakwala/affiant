namespace Affiant.Core.Tests.Filters;

using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Phase-3 Track A Epic A1 — L2 ATDD acceptance test per PRD §7.1.
/// CLOSED 2026-06-12 by Story 16.8 (Epic 16 closure).
///
/// Contract under test (PRD §7.1): for a realistic user turn that triggers a
/// WriteCreate tool, the framework MUST produce an Affidavit whose Fields[] is
/// non-empty and whose per-field ProvenanceChain.Current.Source is one of
/// { Inferred, UserStated } — never Empty. This is the L2 architectural invariant.
///
/// L2 lands in Epic 16 (Stories 16.1–16.7); this test ratifies the contract end-to-end
/// via the real IAffidavitProjection (SchemaDrivenAffidavitProjection) and the pre-tool
/// InferenceTriggerFilter.
///
/// Originally committed in a failing state at 3679c0f (2026-05-14). Wrapped in
/// [Fact(Skip = "…")] at 57507f4 (2026-05-14) to make Epic-15 CI tolerant.
/// Skip removed and re-authored to use the real L2 surface by Story 16.8.
///
/// Traceability matrix row: L2-AT-001 (passing-state SHA backfilled to
/// docs/implementation-artifacts/track-a/g1-a1-atdd-7.1-handoff.md).
/// </summary>
public class TaskInferenceOrchestratorAffidavitParityTests
{
    private const string SyntheticUserTurn =
        "Please create a high-priority work order to investigate the left engine "
        + "hydraulic leak on thing-7.";

    // PRD §7.1's literal recorded JSON for the mock port. Values must not be altered.
    private const string PrdSection71JsonLiteral = """
        {
          "Title":     { "value": "Investigate left engine hydraulic leak", "confidence": 0.92 },
          "Priority":  { "value": "High", "confidence": 0.85 },
          "EntityRef": { "value": "thing-7", "confidence": 0.78 }
        }
        """;

    [Fact]
    public async Task Affidavit_BuiltFromContextFabric_HasPopulatedFieldsAndNonEmptyProvenance_ForRealisticUserTurn()
    {
        // ── Arrange: real L2 pipeline via AddAffiantInferenceOrchestration + AddKernel ──

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore();

        // Register the recorded-mock port BEFORE AddAffiantInferenceOrchestration so
        // TryAddScoped<IInferenceCompletionPort> is a no-op and the singleton mock wins.
        services.AddSingleton<IInferenceCompletionPort>(new RecordingInferencePort(
            (_, _) => Task.FromResult(JsonDocument.Parse(PrdSection71JsonLiteral).RootElement.Clone())));

        services.AddAffiantInferenceOrchestration();
        services.AddAffiantSkFilters();

        // Register strategy + descriptor. pluginName must match the kernel plugin name below.
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing",
            pluginName: "ThingPlugin");

        // TaskInferenceStep (Singleton, registered by AddAffiantCore) requires ITaskInferenceStrategy.
        // Wire FakeThingStrategy as the singleton strategy explicitly.
        services.AddSingleton<ITaskInferenceStrategy>(sp => sp.GetRequiredService<FakeThingStrategy>());

        // Register Kernel in DI so filters resolve through the same ServiceProvider.
        services.AddKernel();

        var sp = services.BuildServiceProvider();

        // Use a scope so Scoped services (InferenceTriggerFilter, TaskInferenceRunner, etc.)
        // resolve with a consistent per-request lifetime alongside the Kernel.
        var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        // Register the test plugin (CreateThing returns a WriteProposal envelope).
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions(
            "ThingPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => FakeWriteProposalReturn, "CreateThing")]));

        // Host-contract keys per 16.3's InferenceTriggerFilter expectations.
        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(SyntheticUserTurn);
        kernel.Data["ChatHistory"] = chatHistory;
        kernel.Data["ConversationId"] = "test-conv-7-1";
        kernel.Data["AffiantTurnNumber"] = 0;

        // ── Act: drive the real L2 pipeline via kernel.InvokeAsync ──
        // The pre-tool InferenceTriggerFilter (16.3) fires, calls TaskInferenceRunner (16.2),
        // the recorded port returns the PRD §7.1 JSON, TaskInferenceStep merges it into
        // ContextFabric. ContextFabric is a Singleton — all filter resolution paths share it.
        await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        // ── Project the Affidavit via the framework's real IAffidavitProjection ──
        // ContextFabric is Singleton → same instance the filter wrote into.
        var fabric = scope.ServiceProvider.GetRequiredService<Affiant.Core.Services.ContextFabric>();
        var projection = scope.ServiceProvider
            .GetServices<IAffidavitProjection>()
            .First(p => p.EntityType == "Thing");
        var affidavit = projection.Project(fabric, operationType: "WriteCreate",
            warnings: Array.Empty<string>());

        // ── Assert: PRD §7.1 acceptance contract (UNCHANGED from G1 ratification) ──
        Assert.True(
            affidavit.Fields.Length >= 3,
            $"Expected affidavit.Fields.Length >= 3; got {affidavit.Fields.Length}.");

        Assert.True(
            affidavit.AggregateConfidence > 0.5f,
            $"Expected affidavit.AggregateConfidence > 0.5; got {affidavit.AggregateConfidence}.");

        foreach (var field in affidavit.Fields)
        {
            var source = field.Provenance.Current.Source;
            Assert.True(
                source is ProvenanceSource.Inferred or ProvenanceSource.UserStated,
                $"Field '{field.Name}' has ProvenanceSource.{source}; expected Inferred or UserStated.");
        }
    }

    // WriteProposal-shaped return value — the post-tool TaskInferenceMergeFilter (IAutoFunctionInvocationFilter)
    // would parse this and find no matching field keys, silently no-oping the merge. That's correct because
    // the pre-tool InferenceTriggerFilter already populated the fabric via the port before this returns.
    private const string FakeWriteProposalReturn =
        """{"$type":"WriteProposal","ToolName":"CreateThing","EntityType":"Thing","Proposed":{}}""";

    private sealed class RecordingInferencePort : IInferenceCompletionPort
    {
        private readonly Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> _impl;
        public RecordingInferencePort(Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> impl)
            => _impl = impl;
        public Task<JsonElement> CompleteStructuredAsync(InferenceCompletionRequest request, CancellationToken cancellationToken = default)
            => _impl(request, cancellationToken);
    }

    /// <summary>
    /// PRD §7.1 strategy: three fields (Title, Priority, EntityRef).
    /// </summary>
    private sealed class FakeThingStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";

        public IReadOnlyList<TaskInferenceField> Fields =>
        [
            new TaskInferenceField("Title", "string", "Short title of the thing"),
            new TaskInferenceField(
                "Priority", "string", "Priority level",
                Enum: ["Low", "Medium", "High", "Critical"]),
            new TaskInferenceField("EntityRef", "string", "Reference to an existing entity"),
        ];

        public double? MinimumConfidenceThreshold => null;
    }
}
