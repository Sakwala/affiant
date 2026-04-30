namespace Affiant.Core.Services;

using Affiant.Abstractions.Models;

/// <summary>
/// Sealed service class that owns all entity-tracking state for a conversation turn.
/// Extractors upsert EntityRef instances via the public interface;
/// ContextFabric handles merging, provenance chain preservation, and retrieval.
/// Field-level ProvenanceChain tracking supports the confidence-based merge rule
/// in TaskInferenceStep: higher confidence wins; ties break by ProvenanceSource ordinal.
/// </summary>
public sealed class ContextFabric
{
    private readonly Dictionary<string, EntityRef> _entities = new();
    private readonly Dictionary<string, ProvenanceChain> _fieldChains = new();

    /// <summary>
    /// Insert or merge an entity by EntityId. If the entity already exists,
    /// the incoming fields overlay the existing ones while preserving unset fields.
    /// </summary>
    public void Upsert(EntityRef entityRef)
    {
        ArgumentNullException.ThrowIfNull(entityRef);

        if (_entities.TryGetValue(entityRef.EntityId, out var existing))
        {
            _entities[entityRef.EntityId] = MergeEntityRefs(existing, entityRef);
        }
        else
        {
            _entities[entityRef.EntityId] = entityRef;
        }
    }

    /// <summary>Returns the tracked entity for the given EntityId, or null if not present.</summary>
    public EntityRef? GetByKey(string entityKey)
    {
        ArgumentNullException.ThrowIfNull(entityKey);
        _entities.TryGetValue(entityKey, out var entity);
        return entity;
    }

    /// <summary>Returns a shallow copy of all tracked entities, keyed by EntityId.</summary>
    public Dictionary<string, EntityRef> Snapshot() => new(_entities);

    /// <summary>Upserts multiple entities in a single call.</summary>
    public void MergeFrom(IEnumerable<EntityRef> entityRefs)
    {
        ArgumentNullException.ThrowIfNull(entityRefs);
        foreach (var entityRef in entityRefs)
            Upsert(entityRef);
    }

    /// <summary>Clears all tracked entities and field chains. Used for testing and session cleanup.</summary>
    public void Clear()
    {
        _entities.Clear();
        _fieldChains.Clear();
    }

    /// <summary>
    /// Returns the ProvenanceChain for a specific field key, or null if no chain has been recorded.
    /// Field keys are set by TaskInferenceStep using the field name from ITaskInferenceStrategy.
    /// </summary>
    public ProvenanceChain? GetFieldChain(string fieldKey)
    {
        ArgumentNullException.ThrowIfNull(fieldKey);
        _fieldChains.TryGetValue(fieldKey, out var chain);
        return chain;
    }

    /// <summary>
    /// Records or replaces the ProvenanceChain for a field key.
    /// Called by TaskInferenceStep after applying the confidence-based merge rule.
    /// </summary>
    public void SetFieldChain(string fieldKey, ProvenanceChain chain)
    {
        ArgumentNullException.ThrowIfNull(fieldKey);
        ArgumentNullException.ThrowIfNull(chain);
        _fieldChains[fieldKey] = chain;
    }

    private static EntityRef MergeEntityRefs(EntityRef existing, EntityRef incoming)
    {
        var mergedFields = new Dictionary<string, object>(existing.Fields);
        foreach (var (key, value) in incoming.Fields)
            mergedFields[key] = value;

        return new EntityRef(
            EntityType: existing.EntityType,
            EntityId: existing.EntityId,
            DisplayName: incoming.DisplayName,
            Fields: mergedFields);
    }
}
