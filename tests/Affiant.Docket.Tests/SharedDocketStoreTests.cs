using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Docket.Tests.Fixtures;
using Xunit;

namespace Affiant.Docket.Tests;

/// <summary>
/// Shared invariant tests that run against all three IDocketStore implementations.
/// Each [Theory] iterates over InMemory, SQLite, and Postgres via DocketStoreProviderFactory,
/// so a regression in any backend fails the build immediately.
///
/// Three invariants from the framework spec are validated (R1, R3, round-trip):
///   Case 1 — Round-trip preservation: all DocketEntry fields survive file → retrieve.
///   Case 2 — Double-submit guard: UpdateReviewStatusAsync returns 0 when entry is no longer Pending.
///   Case 3 — Expiry idempotency: MarkExpiredAsync called twice does not corrupt state.
///   Case 4 — Amendments round-trip (issue #6): UpdateAmendmentsAsync persists reviewer edits,
///            including an explicit null value for a field the reviewer cleared.
/// </summary>
public sealed class SharedDocketStoreTests
{
    // ── Case 1: Round-trip preservation ─────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task FileDocketEntry_RoundTrip_PreservesAllFields(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault();

        await store.FileDocketEntryAsync(entry, CancellationToken.None);
        var retrieved = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal(entry.EntryId, retrieved.EntryId);
        Assert.Equal(entry.SessionId, retrieved.SessionId);
        Assert.Equal(entry.TenantId, retrieved.TenantId);
        Assert.Equal(entry.UserId, retrieved.UserId);
        Assert.Equal(entry.ReviewerUserId, retrieved.ReviewerUserId);
        Assert.Equal(entry.OperationType, retrieved.OperationType);
        Assert.Equal(entry.Status, retrieved.Status);

        // Provenance round-trip: both fields must survive the JSON serialization path.
        // EF stores serialize ProvenanceChain separately into ProvenanceChainsJson,
        // then re-inject it into the Affidavit fields on load.
        Assert.Equal(entry.Envelope.Fields.Length, retrieved.Envelope.Fields.Length);

        var originalField = entry.Envelope.Fields[0];
        var roundTrippedField = retrieved.Envelope.Fields
            .Single(f => f.Name == originalField.Name);

        Assert.Equal(originalField.Provenance.Current.Source,
            roundTrippedField.Provenance.Current.Source);
        Assert.Equal(originalField.Provenance.Current.Confidence,
            roundTrippedField.Provenance.Current.Confidence);
        Assert.Equal(originalField.Provenance.Current.Evidence,
            roundTrippedField.Provenance.Current.Evidence);

        // Verify second field's provenance (inferred source with non-null turn)
        var originalSecond = entry.Envelope.Fields[1];
        var roundTrippedSecond = retrieved.Envelope.Fields
            .Single(f => f.Name == originalSecond.Name);

        Assert.Equal(originalSecond.Provenance.Current.Source,
            roundTrippedSecond.Provenance.Current.Source);
        Assert.Equal(originalSecond.Provenance.Current.ConversationTurn,
            roundTrippedSecond.Provenance.Current.ConversationTurn);
    }

    // ── Case 2: Double-submit guard ──────────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task UpdateReviewStatus_DoubleSubmitGuard_RejectsUpdateOnNonPendingEntry(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault();
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // First update: entry is Pending → guard passes, 1 row affected
        var firstRows = await store.UpdateReviewStatusAsync(
            entry.EntryId, ReviewStatus.Approved, CancellationToken.None);
        Assert.Equal(1, firstRows);

        var afterFirst = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, afterFirst!.Status);

        // Second update: entry is Approved → guard (WHERE Status = Pending) rejects, 0 rows affected
        var secondRows = await store.UpdateReviewStatusAsync(
            entry.EntryId, ReviewStatus.Rejected, CancellationToken.None);
        Assert.Equal(0, secondRows);

        // Status must remain Approved — the guard prevented the overwrite
        var afterSecond = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, afterSecond!.Status);
    }

    // ── Case 3: Expiry idempotency ───────────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task MarkExpired_CalledTwiceOnSameEntries_IsIdempotent(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.Expired();
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // First tick: identify and mark the expired entry
        var now = DateTimeOffset.UtcNow;
        var expired = await store.ListExpiredAsync(now, CancellationToken.None);
        var ours = expired.Where(e => e.EntryId == entry.EntryId).Select(e => e.EntryId).ToList();
        Assert.Contains(entry.EntryId, ours);

        await store.MarkExpiredAsync(ours, CancellationToken.None);

        var afterFirst = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, afterFirst!.Status);

        // Second tick with the same IDs: the WHERE Status = 'Pending' guard means
        // already-Expired entries are silently skipped — no corruption, no exception
        await store.MarkExpiredAsync(ours, CancellationToken.None);

        var afterSecond = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, afterSecond!.Status);
        Assert.Equal(afterFirst.ExpiresAt, afterSecond.ExpiresAt);
    }

    // ── Case 4: Amendments round-trip (issue #6) ─────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task UpdateAmendments_RoundTrip_PreservesReviewerEditsIncludingExplicitNull(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault();
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // A reviewer amends one field to a new value and explicitly clears another.
        var amendments = new Dictionary<string, object?>
        {
            ["primaryField"] = "reviewer-edited-value",
            ["secondaryField"] = null
        };

        await store.UpdateReviewStatusAsync(entry.EntryId, ReviewStatus.Approved, CancellationToken.None);
        await store.UpdateAmendmentsAsync(entry.EntryId, amendments, CancellationToken.None);

        var retrieved = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.Amendments);
        Assert.Equal(2, retrieved.Amendments!.Count);
        Assert.True(retrieved.Amendments.ContainsKey("secondaryField"));
        Assert.Null(retrieved.Amendments["secondaryField"]);
        Assert.Equal("reviewer-edited-value", retrieved.Amendments["primaryField"]?.ToString());
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task UpdateAmendments_OverwritesPreviouslyRecordedAmendments(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault();
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        await store.UpdateAmendmentsAsync(
            entry.EntryId,
            new Dictionary<string, object?> { ["primaryField"] = "first-edit" },
            CancellationToken.None);
        await store.UpdateAmendmentsAsync(
            entry.EntryId,
            new Dictionary<string, object?> { ["primaryField"] = "second-edit" },
            CancellationToken.None);

        var retrieved = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);

        Assert.NotNull(retrieved!.Amendments);
        Assert.Single(retrieved.Amendments!);
        Assert.Equal("second-edit", retrieved.Amendments["primaryField"]?.ToString());
    }
}
