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

    public Task<int> UpdateReviewStatusAsync(Guid entryId, ReviewStatus status, CancellationToken ct)
    {
        var idx = Filed.FindIndex(e => e.EntryId == entryId && e.Status == ReviewStatus.Pending);
        if (idx < 0) return Task.FromResult(0);
        Filed[idx] = Filed[idx] with { Status = status };
        return Task.FromResult(1);
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

    public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
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
    public Task<ApprovalVerdict?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
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
    public Task<ApprovalVerdict?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
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
