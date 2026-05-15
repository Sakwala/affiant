namespace Affiant.Abstractions.Models;

public sealed record AffidavitEmittedEvent(
    string ConversationId,
    Guid AffidavitId,
    string OperationType,
    string EntityType,
    int PopulatedFieldCount,
    float AggregateConfidence,
    int EmptyProvenanceFieldCount);
