using Affiant.Abstractions.Models;

namespace Affiant.Docket.Tests.Fixtures;

/// <summary>
/// Factory for DocketEntry test data. All positional constructor fields are populated
/// with stable, deterministic values so round-trip assertions are unambiguous.
/// </summary>
public static class TestDocketEntry
{
    /// <summary>
    /// Creates a pending DocketEntry with two AffidavitFields carrying distinct provenance
    /// sources — sufficient to exercise the ProvenanceChainsJson round-trip path in EF stores.
    /// </summary>
    public static DocketEntry CreateDefault(
        Guid? entryId = null,
        string? sessionId = null,
        ReviewStatus status = ReviewStatus.Pending,
        DateTimeOffset? expiresAt = null)
    {
        var userField = new AffidavitField(
            Name: "primaryField",
            Value: "test-value",
            PreviousValue: null,
            Provenance: ProvenanceChain.From(ProvenanceTag.FromUser("primaryField", binding: null)));

        var inferredField = new AffidavitField(
            Name: "secondaryField",
            Value: 42,
            PreviousValue: null,
            Provenance: ProvenanceChain.From(
                new ProvenanceTag(ProvenanceSource.Inferred, 0.7f, "LLM inferred: secondaryField", 1)));

        var affidavit = new Affidavit(
            OperationType: "test-op",
            EntityType: "test-entity",
            EntityId: "entity-001",
            Fields: [userField, inferredField],
            AggregateConfidence: 0.85f,
            PopulatedConfidence: 0.85f,
            EmptyFieldCount: 0,
            Warnings: [],
            RequiresConfirmation: false);

        return new DocketEntry(
            EntryId: entryId ?? Guid.NewGuid(),
            SessionId: sessionId ?? Guid.NewGuid().ToString(),
            TenantId: "tenant-001",
            UserId: "user-001",
            ReviewerUserId: "reviewer-001",
            OperationType: "test-op",
            Envelope: affidavit,
            Status: status,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10),
            Amendments: null);
    }

    /// <summary>Returns a pending entry whose ExpiresAt is already in the past.</summary>
    public static DocketEntry Expired(string? sessionId = null)
        => CreateDefault(sessionId: sessionId, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
}
