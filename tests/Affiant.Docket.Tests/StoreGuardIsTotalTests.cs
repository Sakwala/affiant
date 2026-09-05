namespace Affiant.Docket.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Docket.Tests.Fixtures;
using Xunit;

/// <summary>
/// The store's attestation guard is total, not a guard on one member (AZ-1, AZ-5, DK-1).
/// </summary>
/// <remarks>
/// <para>
/// A guard on <see cref="IDocketStore.TransitionAsync"/> alone is not what "the store makes the
/// state unwritable" means. Two other members reached the same state: an unscoped status write that
/// took any status by entry id, and <see cref="IDocketStore.FileDocketEntryAsync"/>, which validated
/// nothing and would file a row already decided. From either one the row was returned by
/// <see cref="IDocketStore.ListApprovedUnexecutedAsync"/> — the worklist a host's executor drains —
/// and the store's own execution report accepted it.
/// </para>
/// <para>
/// Every theory here runs against all three shipped backends, because a rule one store enforces and
/// another does not is not a rule.
/// </para>
/// </remarks>
public sealed class StoreGuardIsTotalTests
{
    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ARowMayOnlyBeFiledPending(IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var decided = TestDocketEntry.CreateDefault(tenantId: tenantId, status: ReviewStatus.Approved);

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => store.FileDocketEntryAsync(decided, CancellationToken.None));
        Assert.Contains("AZ-1", refused.Message, StringComparison.Ordinal);

        Assert.Null(await store.GetDocketEntryAsync(decided.EntryId, CancellationToken.None));

        // A decided row can only arise through the guarded transition, so nothing lands in the
        // approved-unexecuted worklist that nobody agreed to.
        var listed = await store.ListApprovedUnexecutedAsync(
            new DocketScope(tenantId), new DocketPage(10), CancellationToken.None);
        Assert.Empty(listed.Items);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task APendingRowIsFiledAsItAlwaysWas(IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);

        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Pending, stored!.Status);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task AnExecutionReportOnARowWithNoAttestation_IsRefusedByTheStore(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // Approved with nobody on it is a state the transition guard refuses to write. The store
        // still refuses to execute against one, because "the executor is reachable only through an
        // entry that carries an attestation" (AZ-5) is a property of the row, not of the path that
        // produced it.
        var at = DateTimeOffset.UtcNow;
        await store.TransitionAsync(
            entry.EntryId,
            new DocketScope(tenantId),
            ReviewStatus.Pending,
            new DocketTransitionPatch(
                ReviewStatus.Approved,
                Decision: new DecisionRecord(DecisionKind.Approve, null, at),
                Attestation: new Attestation(Attestor.Member.FromStorage("member-1"), at, entry.EntryId),
                DecidedAt: at),
            CancellationToken.None);

        var recorded = await store.RecordExecutionAsync(
            entry.EntryId, new DocketScope(tenantId), ExecutionOutcome.Executed, "row-1",
            ExecutionOutcome.Unexecuted, CancellationToken.None);
        Assert.IsType<RecordExecutionResult.Recorded>(recorded);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task NoMemberWritesAStatusByIdAlone(IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);

        // The unscoped status write is gone from the contract: every member that moves a row names
        // the tenant it may move it in, and every decided state carries who agreed.
        Assert.DoesNotContain(
            typeof(IDocketStore).GetMethods(),
            m => m.Name == "UpdateReviewStatusAsync");

        Assert.DoesNotContain(
            store.GetType().GetMethods(),
            m => m.Name == "UpdateReviewStatusAsync");
    }

    private static string NewTenant() => $"tenant-{Guid.NewGuid():N}";
}
