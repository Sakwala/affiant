namespace Affiant.Abstractions.Models;

using Affiant.Abstractions.Interfaces;

public sealed record InferenceTriggerContext(
    string FunctionName,
    string? PluginName,
    IReadOnlyDictionary<string, object?> Arguments,
    IContextFabric Fabric,
    InferencePhase Phase);
