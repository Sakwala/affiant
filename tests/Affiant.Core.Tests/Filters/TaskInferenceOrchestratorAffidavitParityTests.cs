namespace Affiant.Core.Tests.Filters;

using System.Linq;
using System.Threading.Tasks;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Phase-3 Track A Epic A1 — L2 ATDD acceptance test per PRD §7.1.
/// Source: docs/architecture/phase-3-prd-l2-inference-orchestration.md §7.1.
///
/// Contract under test (PRD §7.1): for a realistic user turn that triggers a
/// `WriteCreate` tool, the framework MUST produce an Affidavit whose Fields[] is
/// non-empty and whose per-field <see cref="ProvenanceChain.Current"/> Source is
/// one of { <see cref="ProvenanceSource.Inferred"/>, <see cref="ProvenanceSource.UserStated"/> } —
/// never <see cref="ProvenanceSource.Empty"/>. This is the L2 architectural
/// invariant; satisfying it is the green light for closing Epic A2.
///
/// Why this test fails against today's `main` (the design intent — see PRD §7.1
/// last paragraph): the current generic <see cref="TaskInferenceFilter"/> is a
/// post-tool <see cref="IAutoFunctionInvocationFilter"/>. It parses the *tool's
/// return value* for structured-output JSON of shape `{FieldName:{value,confidence}}`
/// and forwards a matching shape to <see cref="TaskInferenceStep"/>. Realistic
/// write tools return a <c>WriteProposal</c> envelope, not that shape, so the
/// merge is a silent no-op. <see cref="ContextFabric"/> stays empty for the
/// strategy's fields, the projection emits <see cref="ProvenanceTag.Empty"/>
/// for each, and the assertions below trip.
///
/// Implementation deviations from PRD §7.1's literal code — Seevali approved
/// (handoff at docs/implementation-artifacts/track-a/g1-a1-atdd-7.1-handoff.md):
///   • PRD references the A0 attribute `[AffiantWriteTool("WriteCreate", "Thing",
///     typeof(FakeThingStrategy))]` — A0 is not yet implemented on main, so the
///     fake tool is registered as a plain `[KernelFunction]` here.
///   • PRD references the A2 port `IInferenceCompletionPort` for the recorded
///     mock — A2 is not yet implemented, so today's path has *no* pre-tool
///     inference at all (the bug). The recorded JSON in PRD §7.1 has nowhere
///     to be consumed today; that absence IS the failure mode this test
///     ratifies.
///   • PRD says "Capture the resulting Affidavit from the test's ReviewGate
///     mock." The framework has no `IAffidavitProjection` today; the test
///     builds the Affidavit through <see cref="ProjectAffidavitFromFabric"/>,
///     which is the moral equivalent of the A2 `SchemaDrivenAffidavitProjection`
///     (PRD §2.4). When A2 lands, replace this helper with
///     `serviceProvider.GetRequiredService&lt;IAffidavitProjection&gt;().Project(...)`
///     and wire `IInferenceCompletionPort` instead of the synthesized auto-context.
///
/// Traceability matrix row: L2-AT-001 (see docs/implementation-artifacts/track-a/
/// traceability-matrix.md). Closes when this test AND the Tier-1 Validator
/// scenario for `INV-AFFIDAVIT-NONEMPTY` pass on the same commit SHA.
/// </summary>
public class TaskInferenceOrchestratorAffidavitParityTests
{
    private const string SyntheticUserTurn =
        "Please create a high-priority work order to investigate the left engine "
        + "hydraulic leak on thing-7.";

    /// <summary>
    /// Realistic write-tool return value. A WriteProposal-shaped envelope — not
    /// `{FieldName:{value,confidence}}` JSON. Today's post-tool TaskInferenceFilter
    /// parses this, finds no fields matching the strategy's schema, and silently
    /// no-ops the merge. ContextFabric stays empty.
    /// </summary>
    private const string FakeWriteProposalReturn =
        """{"$type":"WriteProposal","ToolName":"CreateThing","EntityType":"Thing","Proposed":{}}""";

    [Fact]
    public async Task Affidavit_BuiltFromContextFabric_HasPopulatedFieldsAndNonEmptyProvenance_ForRealisticUserTurn()
    {
        // ── Arrange: today's pipeline (no pre-tool inference; only the buggy post-tool filter)
        var strategy = new FakeThingStrategy();
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(strategy, fabric, NullLogger<TaskInferenceStep>.Instance);
        var taskInferenceFilter = new TaskInferenceFilter(step, NullLogger<TaskInferenceFilter>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.AutoFunctionInvocationFilters.Add(taskInferenceFilter);
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions(
            "ThingPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => FakeWriteProposalReturn, "CreateThing")]));

        // ── Act: simulate a realistic auto-invoked tool call.
        //
        // We drive the IAutoFunctionInvocationFilter chain directly with a synthesized
        // AutoFunctionInvocationContext (per the pattern in
        // Affiant.SemanticKernel.Tests/Filters/DualProviderFilterChainTests). SK 1.74's
        // real auto-invocation loop needs provider-specific FinishReason / ModelId
        // metadata that a bare IChatCompletionService stub cannot supply, so this
        // synthesized path is the deterministic equivalent. The user's prompt
        // intent is carried via the ChatHistory.
        var function = kernel.Plugins["ThingPlugin"]["CreateThing"];
        var fnResult = await kernel.InvokeAsync("ThingPlugin", "CreateThing");
        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(SyntheticUserTurn);
        var assistantToolCallMessage = new ChatMessageContent(AuthorRole.Assistant, string.Empty);
        var autoCtx = new AutoFunctionInvocationContext(
            kernel, function, fnResult, chatHistory, assistantToolCallMessage);

        Func<AutoFunctionInvocationContext, Task> terminal = _ => Task.CompletedTask;
        foreach (var f in kernel.AutoFunctionInvocationFilters.Reverse())
        {
            var captured = f;
            var next = terminal;
            terminal = ctx => captured.OnAutoFunctionInvocationAsync(ctx, next);
        }
        await terminal(autoCtx);

        // Project an Affidavit the way A2's SchemaDrivenAffidavitProjection will:
        // walk strategy.Fields, pull each chain from ContextFabric, emit
        // ProvenanceTag.Empty for fields absent from the fabric.
        var affidavit = ProjectAffidavitFromFabric(strategy, fabric, operationType: "WriteCreate");

        // ── Assert: PRD §7.1 acceptance contract
        Assert.True(
            affidavit.Fields.Length >= 3,
            $"Expected affidavit.Fields.Length >= 3 (one per strategy field); got "
            + $"{affidavit.Fields.Length}. The strategy declares Title / Priority / EntityRef; "
            + "the projection must emit a field per strategy slot even when the fabric is empty.");

        Assert.True(
            affidavit.AggregateConfidence > 0.5f,
            $"Expected affidavit.AggregateConfidence > 0.5; got {affidavit.AggregateConfidence}. "
            + "AggregateConfidence is 0.0 when every field carries ProvenanceTag.Empty — the "
            + "shape of the empty-Affidavit regression. L2 (Epic A2) fixes this by running "
            + "pre-tool inference and merging into the fabric before projection.");

        foreach (var field in affidavit.Fields)
        {
            var source = field.Provenance.Current.Source;
            Assert.True(
                source is ProvenanceSource.Inferred or ProvenanceSource.UserStated,
                $"Field '{field.Name}' has ProvenanceSource.{source}; expected Inferred or "
                + "UserStated (PRD §7.1). Today's pipeline emits ProvenanceSource.Empty for "
                + "every field because no pre-tool inference populates the fabric — violates "
                + "framework spec normative rule 7 ('Every Affidavit field carries provenance, "
                + "no exceptions'). L2 (Epic A2) fixes this.");
        }
    }

    /// <summary>
    /// Test-only schema-driven projection. Walks <see cref="ITaskInferenceStrategy.Fields"/>,
    /// pulls each field's chain from <see cref="ContextFabric"/>, falls back to
    /// <see cref="ProvenanceTag.Empty"/> when no chain is present. Aggregate confidence is
    /// the mean of per-field confidences over non-empty fields (matches PRD §2.4
    /// and today's host-side `AffidavitMapper.BuildFromWorkOrderFormData`).
    ///
    /// Replace at A2 merge with `IAffidavitProjection.Project(...)` resolved from DI.
    /// </summary>
    private static Affidavit ProjectAffidavitFromFabric(
        ITaskInferenceStrategy strategy,
        ContextFabric fabric,
        string operationType)
    {
        var entity = fabric.GetByKey(strategy.EntityName);

        var fields = strategy.Fields
            .Select(f =>
            {
                var chain = fabric.GetFieldChain(f.Name) ?? ProvenanceChain.From(ProvenanceTag.Empty);
                object? value = null;
                if (chain.Current.Source != ProvenanceSource.Empty
                    && entity is not null
                    && entity.Fields.TryGetValue(f.Name, out var v))
                {
                    value = v;
                }
                return new AffidavitField(
                    Name: f.Name,
                    Value: value,
                    PreviousValue: null,
                    Provenance: chain);
            })
            .ToArray();

        var nonEmpty = fields.Where(f => f.Provenance.Current.Source != ProvenanceSource.Empty).ToArray();
        var aggregateConfidence = nonEmpty.Length == 0
            ? 0f
            : nonEmpty.Average(f => f.Provenance.Current.Confidence);

        return new Affidavit(
            OperationType: operationType,
            EntityType: strategy.EntityName,
            EntityId: null,
            Fields: fields,
            AggregateConfidence: aggregateConfidence,
            Warnings: Array.Empty<string>(),
            RequiresConfirmation: true);
    }

    /// <summary>
    /// Test fake strategy matching the PRD §7.1 setup: three fields (Title, Priority,
    /// EntityRef). When A2 lands and tests use the real attribute-driven registration,
    /// the strategy class itself stays; only the registration surface changes.
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
