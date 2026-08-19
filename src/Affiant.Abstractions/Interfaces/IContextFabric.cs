namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// Read/write access to the per-turn context fabric: the accumulated entity state
/// (<see cref="EntityRef"/>) and per-field provenance (<see cref="ProvenanceChain"/>) the framework
/// builds up as a conversation progresses, and that an <see cref="IAffidavitProjection"/> reads
/// when it projects an <see cref="Affidavit"/> for review.
/// </summary>
/// <remarks>
/// Hosts consume this interface (from a filter, an extractor, or an <see cref="IFieldResolver"/>);
/// they rarely implement it. The concrete <c>ContextFabric</c> lives in
/// <c>Affiant.Core.Services</c> and is registered by <c>AddAffiantCore</c>. The interface exists at
/// the abstractions layer so L2 contracts can reference fabric state without inverting the package
/// layering — it was introduced 2026-05-16 for exactly that reason.
/// </remarks>
public interface IContextFabric
{
    ProvenanceChain? GetFieldChain(string fieldName);
    void SetFieldChain(string fieldName, ProvenanceChain chain);
    EntityRef? GetByKey(string key);
    void Upsert(EntityRef entity);
}
