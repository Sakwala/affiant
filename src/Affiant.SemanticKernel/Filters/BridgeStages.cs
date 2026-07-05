namespace Affiant.SemanticKernel.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Filters;

/// <summary>
/// Partitions the neutral filter chain into the two firing positions Semantic Kernel exposes.
/// SK is the only backend with this split (its function-invocation vs auto-function-invocation
/// filter interfaces); the neutral contract stays position-agnostic, so this SK-specific
/// knowledge lives here in the adapter rather than on <see cref="IToolInvocationFilter"/>.
///
/// The completion stage is exactly the two filters that require the auto-invocation loop's
/// result-replacement / termination authority. Everything else — including host
/// <see cref="ContextExtractor"/> subclasses — runs at the invocation stage.
/// </summary>
internal static class BridgeStages
{
    public static IReadOnlyList<IToolInvocationFilter> InvocationStage(
        IReadOnlyList<IToolInvocationFilter> all) =>
        all.Where(f => !IsCompletionStage(f)).ToList();

    public static IReadOnlyList<IToolInvocationFilter> CompletionStage(
        IReadOnlyList<IToolInvocationFilter> all) =>
        all.Where(IsCompletionStage).ToList();

    private static bool IsCompletionStage(IToolInvocationFilter filter) =>
        filter is TaskInferenceMergeFilter or ReviewGateFilter;
}
