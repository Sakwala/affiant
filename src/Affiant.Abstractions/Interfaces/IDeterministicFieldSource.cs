namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IDeterministicFieldSource
{
    string FieldName { get; }

    ProvenanceTag? Resolve(IContextFabric fabric);
}
