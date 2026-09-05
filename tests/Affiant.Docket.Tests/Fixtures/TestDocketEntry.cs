using Affiant.Abstractions.Interfaces;
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
        DateTimeOffset? expiresAt = null,
        string? tenantId = null,
        DateTimeOffset? createdAt = null,
        ExecutionOutcome? execution = null,
        BlockedMarker? blocked = null,
        DateTimeOffset? decidedAt = null)
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

        // An approved row carries an execution outcome and every terminal row records when it left
        // pending — the two correlations the row's shape cannot state but every store enforces.
        var resolvedExecution = status == ReviewStatus.Approved
            ? execution ?? ExecutionOutcome.Unexecuted
            : (ExecutionOutcome?)null;
        var created = createdAt ?? DateTimeOffset.UtcNow;

        return new DocketEntry(
            EntryId: entryId ?? Guid.NewGuid(),
            SessionId: sessionId ?? Guid.NewGuid().ToString(),
            TenantId: tenantId ?? "tenant-001",
            UserId: "user-001",
            ReviewerUserId: "reviewer-001",
            OperationType: "test-op",
            Envelope: affidavit,
            Status: status,
            CreatedAt: created,
            ExpiresAt: expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10),
            Amendments: null,
            Execution: resolvedExecution,
            Blocked: blocked,
            DecidedAt: decidedAt ?? (status == ReviewStatus.Pending ? null : (DateTimeOffset?)created));
    }

    /// <summary>Returns a pending entry whose ExpiresAt is already in the past.</summary>
    public static DocketEntry Expired(string? sessionId = null, string? tenantId = null)
        => CreateDefault(
            sessionId: sessionId, tenantId: tenantId, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));

    /// <summary>
    /// Files a row and moves it to <paramref name="status"/> the only way a row leaves pending: the
    /// guarded transition, carrying who agreed (AZ-1).
    /// </summary>
    /// <remarks>
    /// A store refuses a row filed in any state but pending, so a test that wants a decided row
    /// makes one the way the framework does. Expiry carries no attestation — nobody decided it.
    /// </remarks>
    public static async Task<DocketEntry> FileDecidedAsync(
        IDocketStore store,
        ReviewStatus status,
        DocketEntry? entry = null,
        DateTimeOffset? decidedAt = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var row = entry ?? CreateDefault();
        await store.FileDocketEntryAsync(row, ct);

        var at = decidedAt ?? row.CreatedAt;
        var patch = status == ReviewStatus.Expired
            ? new DocketTransitionPatch(ReviewStatus.Expired)
            : new DocketTransitionPatch(
                status,
                Decision: new DecisionRecord(
                    status == ReviewStatus.Approved ? DecisionKind.Approve : DecisionKind.Reject, null, at),
                Attestation: new Attestation(Attestor.Member.FromStorage("member-1"), at, row.EntryId),
                DecidedAt: at);

        var moved = await store.TransitionAsync(
            row.EntryId,
            new DocketScope(row.TenantId),
            ReviewStatus.Pending,
            patch,
            ct);

        return moved is DocketTransitionResult.Transitioned t
            ? t.Entry
            : throw new InvalidOperationException($"could not move the row to {status}: {moved.GetType().Name}");
    }
}
