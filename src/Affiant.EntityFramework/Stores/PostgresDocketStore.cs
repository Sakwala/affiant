using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.Logging;
using AbstractConversationContext = Affiant.Abstractions.Models.ConversationContext;

namespace Affiant.EntityFramework.Stores;

/// <summary>
/// PostgreSQL-backed <see cref="IDocketStore"/>.
/// </summary>
/// <remarks>
/// The contract itself lives in <see cref="EfDocketOperations"/>, which this and
/// <see cref="SqliteDocketStore"/> share verbatim: the two backends used to carry near-copies of
/// the same code that had already begun to diverge, and a store whose behaviour differs from the one
/// the fixtures were written against is not a second implementation of the contract but a second
/// contract. This type exists so a host still registers a provider-named store and so each provider
/// keeps its own logger category.
/// </remarks>
/// <param name="db">The Affiant EF Core context.</param>
/// <param name="logger">Logger for the filing-race diagnostics.</param>
/// <param name="timeProvider">
/// The clock this store compares <see cref="DocketEntry.ExpiresAt"/> against when it projects expiry
/// onto a read (see <see cref="IDocketStore.GetDocketEntryAsync"/>) and when it stamps a conversation
/// context's last-updated instant. Defaults to <see cref="TimeProvider.System"/>; DI supplies
/// whatever the host registered, and a test substitutes a fake.
/// </param>
public sealed class PostgresDocketStore(
    AffiantDbContext db,
    ILogger<PostgresDocketStore> logger,
    TimeProvider? timeProvider = null) : IDocketStore
{
    private readonly EfDocketOperations _docket =
        new(db, logger, timeProvider ?? TimeProvider.System);

    public Task SaveContextAsync(string sessionId, AbstractConversationContext context, CancellationToken ct)
        => _docket.SaveContextAsync(sessionId, context, ct);

    public Task<AbstractConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
        => _docket.LoadContextAsync(sessionId, ct);

    public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
        => _docket.FileDocketEntryAsync(entry, ct);

    public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
        => _docket.GetDocketEntryAsync(entryId, ct);

    public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
        => _docket.ConsumeForResubmitAsync(entryId, newEntryId, ct);

    public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
        => _docket.GetResubmissionParentAsync(entryId, ct);

    public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
        => _docket.ListPendingBySessionAsync(sessionId, ct);

    public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
        => _docket.ListAllPendingAsync(ct);

    public Task<DocketTransitionResult> TransitionAsync(
        Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
        => _docket.TransitionAsync(entryId, scope, expected, patch, ct);

    public Task<PreserveAmendmentsResult> PreserveAmendmentsAsync(
        Guid entryId,
        DocketScope scope,
        IReadOnlyDictionary<string, object?> amendments,
        PreservedAct act,
        CancellationToken ct)
        => _docket.PreserveAmendmentsAsync(entryId, scope, amendments, act, ct);

    public Task<RecordExecutionResult> RecordExecutionAsync(
        Guid entryId,
        DocketScope scope,
        ExecutionOutcome outcome,
        string? detail,
        ExecutionOutcome expected,
        CancellationToken ct)
        => _docket.RecordExecutionAsync(entryId, scope, outcome, detail, expected, ct);

    public Task<RecordSupersessionResult> RecordSupersessionAsync(
        Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
        => _docket.RecordSupersessionAsync(entryId, scope, supersededBy, ct);

    public Task<int> MarkBlockedAsync(
        Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
        => _docket.MarkBlockedAsync(entryId, scope, marker, ct);

    public Task<long> CountPendingAsync(CancellationToken ct) => _docket.CountPendingAsync(ct);

    public Task<DocketPageResult<DocketEntry>> ListPendingAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
        => _docket.ListPendingAsync(scope, page, ct);

    public Task<DocketPageResult<DocketEntry>> ListApprovedUnexecutedAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
        => _docket.ListApprovedUnexecutedAsync(scope, page, ct);

    public Task<ExpireDueResult> ExpireDueAsync(
        DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
        => _docket.ExpireDueAsync(now, scope, limit, ct);

    public Task<RetentionResult> ApplyRetentionAsync(
        DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
        => _docket.ApplyRetentionAsync(policy, scope, limit, ct);

    public Task<int> PurgeTenantAsync(string tenantId, CancellationToken ct)
        => _docket.PurgeTenantAsync(tenantId, ct);

    public IAsyncEnumerable<DocketEntry> ExportAsync(DocketScope scope, CancellationToken ct)
        => _docket.ExportAsync(scope, ct);
}
