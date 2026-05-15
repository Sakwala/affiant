namespace Affiant.Core.Triggers;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// Host-supplied set of function names that should trigger inference. Soft-deprecated on day one;
/// removed before v1.0.0. Use <see cref="WriteIntentInferenceTrigger"/> with the AffiantToolRegistry instead.
/// </summary>
[Obsolete(
    "Use WriteIntentInferenceTrigger with the AffiantToolRegistry from Epic A0 / Story 15.2. " +
    "FunctionNameInferenceTrigger will be removed before v1.0.0 (see L2 PRD §10.3 deprecation timeline).",
    error: false)]
public sealed class FunctionNameInferenceTrigger : IInferenceTrigger
{
    private readonly HashSet<string> _functionNames;

    public FunctionNameInferenceTrigger(IEnumerable<string> functionNames)
    {
        _functionNames = new HashSet<string>(functionNames, StringComparer.Ordinal);
    }

    public bool ShouldRun(InferenceTriggerContext context)
    {
        return context.Phase == InferencePhase.PreTool
            && _functionNames.Contains(context.FunctionName);
    }
}
