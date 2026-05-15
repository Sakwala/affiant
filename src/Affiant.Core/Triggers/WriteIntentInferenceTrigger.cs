namespace Affiant.Core.Triggers;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// Fires inference when the executing tool is registered as a write-intent tool
/// (Operation.Kind is "WriteCreate" or "WriteUpdate") and the pipeline is in the PreTool phase.
/// </summary>
public sealed class WriteIntentInferenceTrigger : IInferenceTrigger
{
    private readonly IAffiantToolRegistry _registry;

    public WriteIntentInferenceTrigger(IAffiantToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public bool ShouldRun(InferenceTriggerContext context)
    {
        var descriptor = _registry.Find(context.FunctionName, context.PluginName);
        if (descriptor is null) return false;
        if (context.Phase != InferencePhase.PreTool) return false;
        var kind = descriptor.Operation.Kind;
        return kind == "WriteCreate" || kind == "WriteUpdate";
    }
}
