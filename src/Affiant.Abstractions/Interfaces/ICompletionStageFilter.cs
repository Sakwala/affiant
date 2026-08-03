namespace Affiant.Abstractions.Interfaces;

/// <summary>
/// Marker for neutral filters that must run at Semantic Kernel's completion-stage seam
/// (<c>IAutoFunctionInvocationFilter</c>, driven by <c>AffiantAutoFunctionInvocationBridge</c>)
/// rather than its invocation-stage seam (<c>IFunctionInvocationFilter</c>). MAF has no such split
/// — every filter, marked or not, runs at its one middleware seam in canonical registration order
/// (framework spec §3.12.4) — so this interface only matters to the SK adapter's
/// <c>Affiant.SemanticKernel.Filters.BridgeStages</c> partition.
///
/// Introduced area-3 P2 (ruling 1) so <c>BridgeStages</c> can identify "which neutral filters belong
/// in the completion stage" structurally instead of hard-coding a closed type list
/// (<c>TaskInferenceMergeFilter</c>/<c>ReviewGateFilter</c>) — a host or test can add a third
/// completion-stage filter by implementing this interface, and the SK bridge partition, the
/// cross-adapter failure-contract guarantee, and the pipeline-order tests all pick it up without
/// further change.
/// </summary>
public interface ICompletionStageFilter : IToolInvocationFilter
{
}
