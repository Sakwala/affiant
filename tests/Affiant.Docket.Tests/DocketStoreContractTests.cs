using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Docket.Tests.Fixtures;
using Xunit;

namespace Affiant.Docket.Tests;

/// <summary>
/// The Docket store contract, one test per rule sentence, run against all three shipped backends.
/// </summary>
/// <remarks>
/// <para>
/// The rules under test are the ones a store — and only a store — can enforce: the guarded
/// compare-and-set out of pending and the three distinct refusals it produces; the execution outcome
/// recorded once; the amendments a refused late decision carried, preserved as their own fact; the
/// lineage written once on a terminal row; tenant isolation reported as not-found rather than
/// forbidden; the bounded, cursor-paged listings; retention that never ages out an approved row whose
/// write has not been reported; purge; and export order.
/// </para>
/// <para>
/// Running every one of them against the in-memory store, SQLite and Postgres is the point: the
/// in-memory store is the reference the other two earn their name by passing the same fixtures as,
/// and a backend that agreed with the gate and with nothing else would be a second contract wearing
/// the first one's interface.
/// </para>
/// <para>
/// Every test files under a tenant of its own. The Postgres container is shared by the whole
/// assembly, so a test that read across tenants would see rows another class filed a moment earlier —
/// which is also exactly what the tenant scope exists to prevent in production.
/// </para>
/// </remarks>
public sealed class DocketStoreContractTests
{
    // ── The guarded compare-and-set (the review-outcome state machine) ────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Transition_OnAPendingEntry_AppliesTheDecisionAndItsLaterFacts(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var decidedAt = DateTimeOffset.UtcNow;
        var result = await store.TransitionAsync(
            entry.EntryId,
            new DocketScope(tenantId),
            ReviewStatus.Pending,
            new DocketTransitionPatch(
                ReviewStatus.Approved,
                Decision: new DecisionRecord(DecisionKind.Approve, "looks right", decidedAt),
                Attestation: new Attestation(new Attestor.Member("member-1"), decidedAt, entry.EntryId),
                DecidedAt: decidedAt),
            CancellationToken.None);

        var transitioned = Assert.IsType<DocketTransitionResult.Transitioned>(result);
        Assert.Equal(ReviewStatus.Approved, transitioned.Entry.Status);

        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, stored!.Status);

        // An approved row carries an execution outcome, and it starts unexecuted: the framework
        // never performs the write, so the only path off this value is the host's own report.
        Assert.Equal(ExecutionOutcome.Unexecuted, stored.Execution);
        Assert.NotNull(stored.DecidedAt);
        Assert.Equal(DecisionKind.Approve, stored.Decision!.Kind);
        Assert.Equal("looks right", stored.Decision.Reason);
        var attestor = Assert.IsType<Attestor.Member>(stored.Attestation!.By);
        Assert.Equal("member-1", attestor.Id);

        // The proposal itself is untouched — the row keeps what the agent said.
        Assert.Equal(entry.Envelope.OperationType, stored.Envelope.OperationType);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Transition_ASecondDecision_IsRefusedAsAlreadyDecided_AndChangesNothing(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);
        var scope = new DocketScope(tenantId);

        Assert.IsType<DocketTransitionResult.Transitioned>(await store.TransitionAsync(
            entry.EntryId, scope, ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Approved), CancellationToken.None));

        var second = await store.TransitionAsync(
            entry.EntryId, scope, ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Rejected), CancellationToken.None);

        Assert.IsType<DocketTransitionResult.AlreadyDecided>(second);

        // Refused, never applied and never silently overwritten.
        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Approved, stored!.Status);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Transition_OnARowPastItsDeadline_IsRefusedAsExpired_SweptOrNot(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();

        // Persisted Pending, deadline already passed, and no sweep has run.
        var entry = TestDocketEntry.Expired(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var result = await store.TransitionAsync(
            entry.EntryId, new DocketScope(tenantId), ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Approved), CancellationToken.None);

        Assert.IsType<DocketTransitionResult.Expired>(result);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Transition_FromAnotherTenant_IsNotFound_NotForbidden(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var result = await store.TransitionAsync(
            entry.EntryId, new DocketScope(NewTenant()), ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Approved), CancellationToken.None);

        // Indistinguishable from an id that does not exist: anything else leaks the existence of
        // another tenant's rows to whoever can guess an id.
        Assert.IsType<DocketTransitionResult.NotFound>(result);

        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Pending, stored!.Status);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Transition_WithTheStoreWideScope_IsRefusedOutright(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault(tenantId: NewTenant());
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // The store-wide scope belongs to the host's own sweep, retention and export. A decision
        // that could use it would be a decision reaching a row without naming the tenant it belongs
        // to, which is the check every host hand-rolls and gets wrong.
        await Assert.ThrowsAsync<ArgumentException>(() => store.TransitionAsync(
            entry.EntryId, DocketScope.EntireStore, ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Approved), CancellationToken.None));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Transition_OnABlockedEntry_IsRefused_ButTheSweepMayStillExpireIt(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId, expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);
        Assert.Equal(1, await store.MarkBlockedAsync(
            entry.EntryId,
            new BlockedMarker.RequirementNotImplemented(ReviewRequirement.MultiParty),
            CancellationToken.None));

        var scope = new DocketScope(tenantId);
        var decision = await store.TransitionAsync(
            entry.EntryId, scope, ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Approved), CancellationToken.None);

        // Never decided, never executed, never degraded to a weaker requirement.
        Assert.IsType<DocketTransitionResult.AlreadyDecided>(decision);
        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Pending, stored!.Status);
        var blocked = Assert.IsType<BlockedMarker.RequirementNotImplemented>(stored.Blocked);
        Assert.Equal(ReviewRequirement.MultiParty, blocked.Level);

        // It still runs out of time like any other row.
        var expiry = await store.TransitionAsync(
            entry.EntryId, scope, ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Expired), CancellationToken.None);
        Assert.IsType<DocketTransitionResult.Transitioned>(expiry);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task MarkBlocked_IsWrittenOnce_AndNeverOverwritten(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault(tenantId: NewTenant());
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        Assert.Equal(1, await store.MarkBlockedAsync(
            entry.EntryId,
            new BlockedMarker.RequirementNotImplemented(ReviewRequirement.MultiParty),
            CancellationToken.None));

        // A marker that could be overwritten could be cleared, and a row whose blocked marker was
        // cleared is a row that became decidable without anyone deciding it should be.
        Assert.Equal(0, await store.MarkBlockedAsync(
            entry.EntryId,
            new BlockedMarker.CoverageRefused(CoverageCategory.NoExecute, "tool"),
            CancellationToken.None));

        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.IsType<BlockedMarker.RequirementNotImplemented>(stored!.Blocked);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Blocked_CoverageRefusal_RoundTripsItsToolAndCategory(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = TestDocketEntry.CreateDefault(tenantId: NewTenant());
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        await store.MarkBlockedAsync(
            entry.EntryId,
            new BlockedMarker.CoverageRefused(CoverageCategory.ProviderExecuted, "provider.write"),
            CancellationToken.None);

        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        var blocked = Assert.IsType<BlockedMarker.CoverageRefused>(stored!.Blocked);

        // The tool name is on the row so coverage can be re-assessed on a resubmission.
        Assert.Equal("provider.write", blocked.ToolName);
        Assert.Equal(CoverageCategory.ProviderExecuted, blocked.Category);
    }

    // ── The execution outcome, recorded once ─────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task RecordExecution_OnAnApprovedEntry_RecordsTheOutcomeAndKeepsTheApproval(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var scope = new DocketScope(tenantId);
        var entry = await ApprovedEntryAsync(store, tenantId);

        var result = await store.RecordExecutionAsync(
            entry.EntryId, scope, ExecutionOutcome.Failed, "the domain store rejected it",
            ExecutionOutcome.Unexecuted, CancellationToken.None);

        var recorded = Assert.IsType<RecordExecutionResult.Recorded>(result);

        // An approved-but-failed write stays distinguishable from an approved-and-committed one, and
        // the approval itself is untouched: the write was authorised either way.
        Assert.Equal(ReviewStatus.Approved, recorded.Entry.Status);
        Assert.Equal(ExecutionOutcome.Failed, recorded.Entry.Execution);
        Assert.Equal("the domain store rejected it", recorded.Entry.ExecutionDetail);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task RecordExecution_ASecondReport_IsRefused_AndTheFirstOutcomeStands(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var scope = new DocketScope(tenantId);
        var entry = await ApprovedEntryAsync(store, tenantId);

        Assert.IsType<RecordExecutionResult.Recorded>(await store.RecordExecutionAsync(
            entry.EntryId, scope, ExecutionOutcome.Executed, null,
            ExecutionOutcome.Unexecuted, CancellationToken.None));

        var second = await store.RecordExecutionAsync(
            entry.EntryId, scope, ExecutionOutcome.Failed, "a retry that should not land",
            ExecutionOutcome.Unexecuted, CancellationToken.None);

        // Without the guard, an executed row could be flipped to failed by a later caller — an edit
        // in place of a recorded fact, and an audit record that lies. A host that retries a write
        // reports once, when it knows the outcome.
        Assert.IsType<RecordExecutionResult.ExecutionAlreadyRecorded>(second);

        var stored = await store.GetDocketEntryAsync(entry.EntryId, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Executed, stored!.Execution);
        Assert.Null(stored.ExecutionDetail);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task RecordExecution_OnAPendingEntry_IsRefusedAsNotApproved(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var result = await store.RecordExecutionAsync(
            entry.EntryId, new DocketScope(tenantId), ExecutionOutcome.Executed, null,
            ExecutionOutcome.Unexecuted, CancellationToken.None);

        // There is no authorised write behind a pending row, so there is nothing to report on.
        Assert.IsType<RecordExecutionResult.NotApproved>(result);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task RecordExecution_FromAnotherTenant_IsNotFound(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var entry = await ApprovedEntryAsync(store, NewTenant());

        var result = await store.RecordExecutionAsync(
            entry.EntryId, new DocketScope(NewTenant()), ExecutionOutcome.Executed, null,
            ExecutionOutcome.Unexecuted, CancellationToken.None);

        Assert.IsType<RecordExecutionResult.NotFound>(result);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task RecordExecution_ReportingUnexecuted_IsACallerError(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = await ApprovedEntryAsync(store, tenantId);

        // Unexecuted is where an approved row starts; an executor reports Executed or Failed.
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordExecutionAsync(
            entry.EntryId, new DocketScope(tenantId), ExecutionOutcome.Unexecuted, null,
            ExecutionOutcome.Unexecuted, CancellationToken.None));
    }

    // ── The amendments a refused late decision carried ───────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task PreserveAmendments_OnAnExpiredEntry_AppendsThemWithTheActThatCarriedThem(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.Expired(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = await store.PreserveAmendmentsAsync(
            entry.EntryId,
            new DocketScope(tenantId),
            new Dictionary<string, object?> { ["primaryField"] = "corrected", ["secondaryField"] = null },
            new PreservedAct(at, "member-1"),
            CancellationToken.None);

        var preserved = Assert.IsType<PreserveAmendmentsResult.Preserved>(result);

        // The act's own instant and principal, not the store's clock and not the row's deadline: a
        // resubmission prefills these as that person's correction, and a record dated to the sweep
        // would place it at a moment nobody typed anything.
        Assert.Equal(at, preserved.Entry.PreservedAmendments!.At);
        Assert.Equal("member-1", preserved.Entry.PreservedAmendments.By);
        Assert.Equal("corrected", preserved.Entry.PreservedAmendments.Amendments["primaryField"]);

        // A null value means the reviewer CLEARED the field, and an absent key means untouched. The
        // two are never conflated, so a cleared field is present with a null value.
        Assert.True(preserved.Entry.PreservedAmendments.Amendments.ContainsKey("secondaryField"));
        Assert.Null(preserved.Entry.PreservedAmendments.Amendments["secondaryField"]);

        // Nothing else moved: this is an appended fact on a terminal row, not an edit of a recorded
        // decision, and what an approval ACCEPTED is a different fact that nobody wrote here.
        Assert.Equal(ReviewStatus.Expired, preserved.Entry.Status);
        Assert.Null(preserved.Entry.Amendments);
        Assert.Null(preserved.Entry.Decision);
        Assert.Null(preserved.Entry.Attestation);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task PreserveAmendments_OnARowThatIsNotExpired_IsRefused(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var result = await store.PreserveAmendmentsAsync(
            entry.EntryId,
            new DocketScope(tenantId),
            new Dictionary<string, object?> { ["primaryField"] = "corrected" },
            new PreservedAct(DateTimeOffset.UtcNow, "member-1"),
            CancellationToken.None);

        // A decision that was not refused as expired has no amendments to preserve.
        Assert.IsType<PreserveAmendmentsResult.NotExpired>(result);
    }

    // ── Lineage ─────────────────────────────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task RecordSupersession_OnATerminalRow_NamesTheSuccessorOnce(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var scope = new DocketScope(tenantId);
        var entry = TestDocketEntry.Expired(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var successor = Guid.NewGuid();
        var result = await store.RecordSupersessionAsync(
            entry.EntryId, scope, successor, CancellationToken.None);

        var recorded = Assert.IsType<RecordSupersessionResult.Recorded>(result);

        // The superseded row keeps its terminal state and records what it became, so the history
        // reads forward from either end.
        Assert.Equal(successor, recorded.Entry.Lineage.SupersededBy);
        Assert.Null(recorded.Entry.Lineage.Supersedes);

        // Once only — the claim is what two concurrent resubmissions of the same row race on.
        Assert.IsType<RecordSupersessionResult.NotTerminal>(await store.RecordSupersessionAsync(
            entry.EntryId, scope, Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task RecordSupersession_OnAPendingRow_IsRefused(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var entry = TestDocketEntry.CreateDefault(tenantId: tenantId);
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        // An entry that is not expired cannot be resubmitted.
        Assert.IsType<RecordSupersessionResult.NotTerminal>(await store.RecordSupersessionAsync(
            entry.EntryId, new DocketScope(tenantId), Guid.NewGuid(), CancellationToken.None));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Lineage_ReadsForwardFromBothEnds(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var superseded = TestDocketEntry.Expired(tenantId: tenantId);
        await store.FileDocketEntryAsync(superseded, CancellationToken.None);

        var successorId = Guid.NewGuid();
        await store.RecordSupersessionAsync(
            superseded.EntryId, new DocketScope(tenantId), successorId, CancellationToken.None);
        await store.FileDocketEntryAsync(
            TestDocketEntry.CreateDefault(entryId: successorId, tenantId: tenantId) with
            {
                Supersedes = superseded.EntryId
            },
            CancellationToken.None);

        var oldRow = await store.GetDocketEntryAsync(superseded.EntryId, CancellationToken.None);
        var newRow = await store.GetDocketEntryAsync(successorId, CancellationToken.None);

        Assert.Equal(successorId, oldRow!.Lineage.SupersededBy);
        Assert.Equal(superseded.EntryId, newRow!.Lineage.Supersedes);
    }

    // ── The bounded, cursor-paged listings ──────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ListPending_IsPagedWithAnOpaqueCursor_InFilingOrder(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var origin = DateTimeOffset.UtcNow.AddHours(-1);
        var filed = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var entry = TestDocketEntry.CreateDefault(
                tenantId: tenantId,
                createdAt: origin.AddMinutes(i),
                expiresAt: DateTimeOffset.UtcNow.AddHours(1));
            await store.FileDocketEntryAsync(entry, CancellationToken.None);
            filed.Add(entry.EntryId);
        }

        var scope = new DocketScope(tenantId);
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await store.ListPendingAsync(
                scope, new DocketPage(2, cursor), CancellationToken.None);
            seen.AddRange(page.Items.Select(e => e.EntryId));
            cursor = page.Cursor;
            pages++;
            Assert.True(pages < 10, "the listing did not drain");
        }
        while (cursor is not null);

        // Filing order, every row exactly once, and no page larger than the limit.
        Assert.Equal(filed, seen);
        Assert.True(pages >= 3);
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ListPending_ExcludesRowsPastTheirDeadline_SweptOrNot(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        await store.FileDocketEntryAsync(TestDocketEntry.Expired(tenantId: tenantId), CancellationToken.None);
        var live = TestDocketEntry.CreateDefault(tenantId: tenantId, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        await store.FileDocketEntryAsync(live, CancellationToken.None);

        var page = await store.ListPendingAsync(
            new DocketScope(tenantId), new DocketPage(50), CancellationToken.None);

        // Which is also what keeps a lapsed entry from being rehydrated as pending on reconnect.
        Assert.Equal([live.EntryId], page.Items.Select(e => e.EntryId));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ListPending_WithAnotherListingsCursor_IsRefused(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var foreign = DocketCursor.Encode(
            DocketCursor.ApprovedUnexecutedListing, DateTimeOffset.UtcNow, Guid.NewGuid());

        // Refusing beats silently restarting: a caller paging with the wrong cursor would otherwise
        // re-read the first page forever without ever being told.
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListPendingAsync(
            new DocketScope(NewTenant()), new DocketPage(2, foreign), CancellationToken.None));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ListApprovedUnexecuted_HoldsTheWorkOutstanding_AndDropsWhatWasReported(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var scope = new DocketScope(tenantId);
        var outstanding = await ApprovedEntryAsync(store, tenantId);
        var reported = await ApprovedEntryAsync(store, tenantId);
        await store.RecordExecutionAsync(
            reported.EntryId, scope, ExecutionOutcome.Executed, null,
            ExecutionOutcome.Unexecuted, CancellationToken.None);

        var page = await store.ListApprovedUnexecutedAsync(
            scope, new DocketPage(50), CancellationToken.None);

        // An approved write nobody has reported on is work outstanding, and after a restart this is
        // the only record that the work exists.
        Assert.Equal([outstanding.EntryId], page.Items.Select(e => e.EntryId));
    }

    // ── The bounded sweep ───────────────────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task ExpireDue_IsScopedToWhatTheHostAsksFor(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var mine = NewTenant();
        var theirs = NewTenant();
        var mineEntry = TestDocketEntry.Expired(tenantId: mine);
        var theirsEntry = TestDocketEntry.Expired(tenantId: theirs);
        await store.FileDocketEntryAsync(mineEntry, CancellationToken.None);
        await store.FileDocketEntryAsync(theirsEntry, CancellationToken.None);

        var result = await store.ExpireDueAsync(
            DateTimeOffset.UtcNow, new DocketScope(mine), 50, CancellationToken.None);

        Assert.Equal([mineEntry.EntryId], result.Expired.Select(e => e.EntryId));

        // The other tenant's row is untouched — it says Pending until its own sweep reaches it, even
        // though every read of it already reports expired.
        var untouched = await store.GetDocketEntryAsync(theirsEntry.EntryId, CancellationToken.None);
        Assert.Equal(ReviewStatus.Expired, untouched!.Status); // what it READS
        var stillDue = await store.ExpireDueAsync(
            DateTimeOffset.UtcNow, new DocketScope(theirs), 50, CancellationToken.None);
        Assert.Equal([theirsEntry.EntryId], stillDue.Expired.Select(e => e.EntryId)); // what it SAID
    }

    // ── Retention, purge and export ─────────────────────────────────────────

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Retention_NeverAgesOutAnApprovedRowWhoseWriteWasNeverReported(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var scope = new DocketScope(tenantId);
        var longAgo = DateTimeOffset.UtcNow.AddYears(-5);

        // Three ancient terminal rows: one approved and never reported on, one approved and
        // reported, one rejected.
        var unexecuted = await ApprovedEntryAsync(store, tenantId, createdAt: longAgo, decidedAt: longAgo);
        var executed = await ApprovedEntryAsync(store, tenantId, createdAt: longAgo, decidedAt: longAgo);
        await store.RecordExecutionAsync(
            executed.EntryId, scope, ExecutionOutcome.Executed, null,
            ExecutionOutcome.Unexecuted, CancellationToken.None);
        var rejected = TestDocketEntry.CreateDefault(
            tenantId: tenantId, status: ReviewStatus.Rejected, createdAt: longAgo, decidedAt: longAgo);
        await store.FileDocketEntryAsync(rejected, CancellationToken.None);

        var result = await store.ApplyRetentionAsync(
            new DocketRetentionPolicy(DateTimeOffset.UtcNow.AddYears(-1)), scope, 50, CancellationToken.None);

        Assert.Equal(2, result.Removed);
        Assert.False(result.More);

        // The one row retention may never remove: it is the only record that a write was authorised
        // and has not happened.
        Assert.NotNull(await store.GetDocketEntryAsync(unexecuted.EntryId, CancellationToken.None));
        Assert.Null(await store.GetDocketEntryAsync(executed.EntryId, CancellationToken.None));
        Assert.Null(await store.GetDocketEntryAsync(rejected.EntryId, CancellationToken.None));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Retention_LeavesPendingRowsAlone_AndIsBounded(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var scope = new DocketScope(tenantId);
        var longAgo = DateTimeOffset.UtcNow.AddYears(-5);

        var pending = TestDocketEntry.CreateDefault(
            tenantId: tenantId, createdAt: longAgo, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        await store.FileDocketEntryAsync(pending, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await store.FileDocketEntryAsync(
                TestDocketEntry.CreateDefault(
                    tenantId: tenantId, status: ReviewStatus.Rejected,
                    createdAt: longAgo.AddMinutes(i), decidedAt: longAgo.AddMinutes(i)),
                CancellationToken.None);
        }

        var policy = new DocketRetentionPolicy(DateTimeOffset.UtcNow.AddYears(-1));
        var first = await store.ApplyRetentionAsync(policy, scope, 2, CancellationToken.None);
        Assert.Equal(2, first.Removed);
        Assert.True(first.More);

        var second = await store.ApplyRetentionAsync(policy, scope, 2, CancellationToken.None);
        Assert.Equal(1, second.Removed);
        Assert.False(second.More);

        // A row still awaiting a decision is not the host's to age out at any age.
        Assert.NotNull(await store.GetDocketEntryAsync(pending.EntryId, CancellationToken.None));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task PurgeTenant_RemovesEverythingOfThatTenantAndNothingElse(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var doomed = NewTenant();
        var neighbour = NewTenant();
        var doomedEntry = TestDocketEntry.CreateDefault(tenantId: doomed);
        var neighbourEntry = TestDocketEntry.CreateDefault(tenantId: neighbour);
        await store.FileDocketEntryAsync(doomedEntry, CancellationToken.None);
        await store.FileDocketEntryAsync(neighbourEntry, CancellationToken.None);

        var removed = await store.PurgeTenantAsync(doomed, CancellationToken.None);

        // A tenant asking for their data to be deleted is asking for all of it; a partial purge is
        // not a purge.
        Assert.Equal(1, removed);
        Assert.Null(await store.GetDocketEntryAsync(doomedEntry.EntryId, CancellationToken.None));
        Assert.NotNull(await store.GetDocketEntryAsync(neighbourEntry.EntryId, CancellationToken.None));
    }

    [Theory]
    [ClassData(typeof(DocketStoreProviderFactory))]
    public async Task Export_StreamsEveryRowInScope_InFilingOrder(
        IDocketStore store, string providerName)
    {
        Assert.NotEmpty(providerName);
        var tenantId = NewTenant();
        var origin = DateTimeOffset.UtcNow.AddHours(-1);
        var filed = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var entry = TestDocketEntry.CreateDefault(tenantId: tenantId, createdAt: origin.AddMinutes(i));
            await store.FileDocketEntryAsync(entry, CancellationToken.None);
            filed.Add(entry.EntryId);
        }

        var exported = new List<Guid>();
        await foreach (var entry in store.ExportAsync(new DocketScope(tenantId), CancellationToken.None))
            exported.Add(entry.EntryId);

        Assert.Equal(filed, exported);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A tenant nothing else in the assembly is using, so a shared database stays honest.</summary>
    private static string NewTenant() => Guid.NewGuid().ToString();

    /// <summary>A filed entry taken through the guarded transition to approved-and-unexecuted.</summary>
    private static async Task<DocketEntry> ApprovedEntryAsync(
        IDocketStore store,
        string tenantId,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? decidedAt = null)
    {
        var entry = TestDocketEntry.CreateDefault(
            tenantId: tenantId,
            createdAt: createdAt,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        await store.FileDocketEntryAsync(entry, CancellationToken.None);

        var result = await store.TransitionAsync(
            entry.EntryId,
            new DocketScope(tenantId),
            ReviewStatus.Pending,
            new DocketTransitionPatch(ReviewStatus.Approved, DecidedAt: decidedAt),
            CancellationToken.None);

        return Assert.IsType<DocketTransitionResult.Transitioned>(result).Entry;
    }
}
