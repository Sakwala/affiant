namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

// Introduced 2026-05-16 (Story 16.1) to let L2 abstractions reference fabric state
// from the DAG root without inverting the package layering (Abstractions → Core).
// Concrete ContextFabric lives in Affiant.Core.Services.
public interface IContextFabric
{
    ProvenanceChain? GetFieldChain(string fieldName);
    void SetFieldChain(string fieldName, ProvenanceChain chain);
    EntityRef? GetByKey(string key);
    void Upsert(EntityRef entity);
}
