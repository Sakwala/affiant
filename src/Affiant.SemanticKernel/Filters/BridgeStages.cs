namespace Affiant.SemanticKernel.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Filters;

/// <summary>
/// Partitions the neutral filter chain into the two firing positions Semantic Kernel exposes.
/// SK is the only backend with this split (its function-invocation vs auto-function-invocation
/// filter interfaces); the neutral contract stays position-agnostic, so this SK-specific
/// knowledge lives here in the adapter rather than on <see cref="IToolInvocationFilter"/>.
///
/// The completion stage is every filter that implements <see cref="ICompletionStageFilter"/> (today:
/// <see cref="TaskInferenceMergeFilter"/>, <see cref="ReviewGateFilter"/> — identified structurally
/// via the marker interface rather than a closed type list, so a host or test can add a third
/// completion-stage filter without touching this class) — these require the auto-invocation loop's
/// result-replacement / termination authority — PLUS <see cref="ToolErrorFilter"/> (area-3 P2 ruling
/// 1), so an exception from any completion-stage filter is converted to a typed
/// <see cref="Affiant.Abstractions.Models.ToolError"/> here exactly as it would be at the
/// invocation-stage/MAF seam, rather than propagating raw into SK's auto-invocation loop.
/// <see cref="ToolErrorFilter"/> is NOT part of <see cref="IsCompletionStage"/> — it is added
/// explicitly in <see cref="CompletionStage"/> only — so it is correctly excluded from neither
/// stage: it already runs at the invocation stage (unaffected) and now ALSO runs at the completion
/// stage.
///
/// <para>
/// <b>Retry safety at this seam (area-3 P2 fix round — corrects a disproven claim).</b> An earlier
/// version of these remarks claimed this participation "cannot double-fire a retry that re-executes
/// the underlying tool." That was FALSE: at this seam, <c>next(context)</c> as seen by the terminal
/// built in <c>AffiantAutoFunctionInvocationBridge</c> is SK's OWN auto-invocation continuation —
/// not the tool — which nested-invokes the real tool through a separate
/// <see cref="Affiant.Abstractions.Models.ToolInvocationContext"/> at the invocation-stage seam. If
/// that continuation throws before the nested invocation happens,
/// <see cref="Affiant.Abstractions.Models.ToolInvocationContext.ToolExecuted"/> is still
/// <see langword="false"/>, and a naive retry would call the continuation a second time — genuinely
/// re-executing the tool. Two independent adversarial refuters reproduced exactly this. The actual
/// guard is <c>AffiantAutoFunctionInvocationBridge</c> setting
/// <see cref="Affiant.Abstractions.Models.ToolInvocationContext.NextIsToolBody"/> to
/// <see langword="false"/> on the <c>ToolInvocationRequest</c> it builds for this stage —
/// <see cref="ToolErrorFilter"/>'s retry branch checks that flag alongside <c>ToolExecuted</c>. See
/// <see cref="Affiant.Abstractions.Models.ToolInvocationContext.NextIsToolBody"/>'s remarks for the
/// full finding.
/// </para>
///
/// Everything else — including host <see cref="ContextExtractor"/> subclasses — runs at the
/// invocation stage only.
/// </summary>
internal static class BridgeStages
{
    public static IReadOnlyList<IToolInvocationFilter> InvocationStage(
        IReadOnlyList<IToolInvocationFilter> all) =>
        all.Where(f => !IsCompletionStage(f)).ToList();

    public static IReadOnlyList<IToolInvocationFilter> CompletionStage(
        IReadOnlyList<IToolInvocationFilter> all) =>
        all.Where(f => f is ToolErrorFilter || IsCompletionStage(f)).ToList();

    private static bool IsCompletionStage(IToolInvocationFilter filter) =>
        filter is ICompletionStageFilter;
}
