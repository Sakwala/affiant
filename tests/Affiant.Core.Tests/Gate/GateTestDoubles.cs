namespace Affiant.Core.Tests.Gate;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;

/// <summary>
/// The doubles the gate-pipeline tests share. Deliberately recording rather than asserting: every
/// rule in this area is about <em>order</em> and <em>what did or did not happen</em>, so the tests
/// read a transcript rather than a mock's expectations.
/// </summary>
internal sealed class RecordingDocketStore : IDocketStore
{
    public List<DocketEntry> Filed { get; } = [];

    /// <summary>Every call this store received, in order, for the pipeline-order assertions.</summary>
    public List<string> Calls { get; } = [];

    public Task FileDocketEntryAsync(DocketEntry entry, CancellationToken ct)
    {
        Calls.Add("file");
        Filed.Add(entry);
        return Task.CompletedTask;
    }

    public Task<DocketEntry?> GetDocketEntryAsync(Guid entryId, CancellationToken ct)
    {
        Calls.Add("get");
        return Task.FromResult(Filed.FirstOrDefault(e => e.EntryId == entryId));
    }


    public Task UpdateAmendmentsAsync(
        Guid entryId, IReadOnlyDictionary<string, object?> amendments, CancellationToken ct)
    {
        var idx = Filed.FindIndex(e => e.EntryId == entryId);
        if (idx >= 0) Filed[idx] = Filed[idx] with { Amendments = amendments };
        return Task.CompletedTask;
    }

    public Task<int> ConsumeForResubmitAsync(Guid entryId, Guid newEntryId, CancellationToken ct)
        => Task.FromResult(0);

    public Task<DocketEntry?> GetResubmissionParentAsync(Guid entryId, CancellationToken ct)
        => Task.FromResult<DocketEntry?>(null);

    public Task SaveContextAsync(string sessionId, ConversationContext context, CancellationToken ct)
        => Task.CompletedTask;

    public Task<ConversationContext?> LoadContextAsync(string sessionId, CancellationToken ct)
        => Task.FromResult<ConversationContext?>(null);

    public Task<IReadOnlyList<DocketEntry>> ListPendingBySessionAsync(string sessionId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

    public Task<IReadOnlyList<DocketEntry>> ListAllPendingAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

    public Task<IReadOnlyList<DocketEntry>> ListExpiredAsync(DateTimeOffset expiresBeforeUtc, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DocketEntry>>([]);

    public Task MarkExpiredAsync(IEnumerable<Guid> entryIds, CancellationToken ct)
        => Task.CompletedTask;

    // ── The scoped, guarded, paged surface ──────────────────────────────
    // Explicit implementations that refuse: this double exists for a test that never reaches the
    // Docket's decision surface, and a stub that quietly answered would let such a test pass
    // against behaviour nobody wrote.
    /// <summary>
    /// The guarded compare-and-set, over this double's own list. Implemented rather than refused
    /// because the filing path reaches it: a Standing Order's approval and its attestation are one
    /// write, so a double that threw here would make an auto-approving test fail at the filing.
    /// </summary>
    Task<DocketTransitionResult> IDocketStore.TransitionAsync(
        Guid entryId, DocketScope scope, ReviewStatus expected, DocketTransitionPatch patch, CancellationToken ct)
    {
        var idx = Filed.FindIndex(e => e.EntryId == entryId);
        if (idx < 0)
            return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.NotFound());
        if (Filed[idx].Status != expected)
            return Task.FromResult<DocketTransitionResult>(new DocketTransitionResult.AlreadyDecided());

        Filed[idx] = Filed[idx] with
        {
            Status = patch.Status,
            Execution = patch.Status == ReviewStatus.Approved
                ? patch.Execution ?? ExecutionOutcome.Unexecuted
                : null,
            Decision = patch.Decision,
            Amendments = patch.Amendments ?? Filed[idx].Amendments,
            AmendedAffidavit = patch.AmendedAffidavit ?? Filed[idx].AmendedAffidavit,
            Attestation = patch.Attestation ?? Filed[idx].Attestation,
            DecidedAt = patch.DecidedAt ?? Filed[idx].DecidedAt,
        };
        return Task.FromResult<DocketTransitionResult>(
            new DocketTransitionResult.Transitioned(Filed[idx]));
    }

    Task<PreserveAmendmentsResult> IDocketStore.PreserveAmendmentsAsync(
        Guid entryId, DocketScope scope, IReadOnlyDictionary<string, object?> amendments,
        PreservedAct act, CancellationToken ct)
        => throw new NotSupportedException();

    Task<RecordExecutionResult> IDocketStore.RecordExecutionAsync(
        Guid entryId, DocketScope scope, ExecutionOutcome outcome, string? detail,
        ExecutionOutcome expected, CancellationToken ct)
        => throw new NotSupportedException();

    Task<RecordSupersessionResult> IDocketStore.RecordSupersessionAsync(
        Guid entryId, DocketScope scope, Guid supersededBy, CancellationToken ct)
        => throw new NotSupportedException();

    Task<int> IDocketStore.MarkBlockedAsync(Guid entryId, DocketScope scope, BlockedMarker marker, CancellationToken ct)
        => Task.FromResult(0);

    Task<DocketPageResult<DocketEntry>> IDocketStore.ListPendingAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
        => Task.FromResult(new DocketPageResult<DocketEntry>([], null, false));

    Task<DocketPageResult<DocketEntry>> IDocketStore.ListApprovedUnexecutedAsync(
        DocketScope scope, DocketPage page, CancellationToken ct)
        => Task.FromResult(new DocketPageResult<DocketEntry>([], null, false));

    Task<ExpireDueResult> IDocketStore.ExpireDueAsync(
        DateTimeOffset now, DocketScope scope, int limit, CancellationToken ct)
        => Task.FromResult(new ExpireDueResult([], false));

    Task<RetentionResult> IDocketStore.ApplyRetentionAsync(
        DocketRetentionPolicy policy, DocketScope scope, int limit, CancellationToken ct)
        => throw new NotSupportedException();

    Task<int> IDocketStore.PurgeTenantAsync(string tenantId, CancellationToken ct)
        => throw new NotSupportedException();

    IAsyncEnumerable<DocketEntry> IDocketStore.ExportAsync(DocketScope scope, CancellationToken ct)
        => throw new NotSupportedException();
}

/// <summary>Records every broadcast so a test can assert a card was — or was not — sent.</summary>
internal sealed class RecordingTransport : IStreamingTransport
{
    public List<(string GroupId, TransportEvent EventType, object Payload)> Broadcasts { get; } = [];

    public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
        => Task.CompletedTask;

    public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
    {
        Broadcasts.Add((groupId, eventType, payload));
        return Task.CompletedTask;
    }

    public Task<DecisionHandOff> AwaitEvidenceCardResponseAsync(
        string sessionGroupId, Guid docketId, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "The non-blocking filing path must never await a reviewer response.");

    /// <summary>The Evidence Cards broadcast, in order.</summary>
    public IReadOnlyList<EvidenceCardRequest> Cards =>
        [.. Broadcasts.Where(b => b.EventType == TransportEvent.EvidenceCardRequest)
                      .Select(b => (EvidenceCardRequest)b.Payload)];
}

/// <summary>A policy that answers with a fixed verdict, and says when it was asked.</summary>
internal sealed class ScriptedPolicy(
    ApprovalVerdict? verdict,
    Action? onEvaluate = null,
    IReadOnlyCollection<ProvenanceSource>? declaredInputs = null,
    TimeSpan? defaultTimeToLive = null) : IApprovalPolicy
{
    public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
    {
        onEvaluate?.Invoke();
        return Task.FromResult(verdict);
    }

    public IReadOnlyCollection<ProvenanceSource> DeclaredInputs => declaredInputs ?? [];

    public TimeSpan? DefaultTimeToLive => defaultTimeToLive;
}

/// <summary>A policy whose evaluation throws — the CV-1 fault no wire-up check can see.</summary>
internal sealed class ThrowingPolicy(Exception? toThrow = null) : IApprovalPolicy
{
    public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
        => throw toThrow ?? new InvalidOperationException("the host's policy is broken");
}

/// <summary>Builds the Affidavits these tests swear to.</summary>
internal static class TestAffidavits
{
    public static AffidavitField Field(
        string name,
        object? value,
        ProvenanceTag? tag = null,
        bool isMandatory = false) =>
        new(name, value, null,
            ProvenanceChain.From(tag ?? ProvenanceTag.FromTool("fixture")),
            IsMandatory: isMandatory);

    public static Affidavit Of(params AffidavitField[] fields) =>
        Affidavit.Create("CreateOrder", "Order", null, fields, []);

    /// <summary>An ordinary, substantive proposal.</summary>
    public static Affidavit Substantive() => Of(Field("title", "Q3 invoice"));
}
