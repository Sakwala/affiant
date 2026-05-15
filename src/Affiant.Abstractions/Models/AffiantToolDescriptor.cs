namespace Affiant.Abstractions.Models;

public sealed record AffiantToolDescriptor(
    string FunctionName,
    string? PluginName,
    Operation Operation,
    string? EntityType,
    Type? InferenceStrategy);
