namespace Affiant.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AffiantWriteToolAttribute : Attribute
{
    public string Operation       { get; }
    public string EntityType      { get; }
    public Type   InferenceStrategy { get; }

    public AffiantWriteToolAttribute(string operation, string entityType, Type inferenceStrategy)
    {
        Operation         = operation;
        EntityType        = entityType;
        InferenceStrategy = inferenceStrategy;
    }
}
