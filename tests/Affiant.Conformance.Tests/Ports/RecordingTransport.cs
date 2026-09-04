using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;

namespace Affiant.Conformance.Tests.Ports;

/// <summary>
/// The transport the driver owns: it records every Evidence Card the gate broadcasts, so
/// <c>expect.card</c> can be answered.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryDeliverResponse"/> returns <c>false</c> deliberately. The interface's default
/// implementation does the same, and a <c>true</c> here would send
/// <see cref="Affiant.Core.Services.ReviewGate.HandleDecisionAsync"/> down the live-waiter path,
/// where the outcome belongs to a caller blocked inside <c>FileReviewAsync</c> and nothing is
/// written. The suite drives the restart path — file, then decide — which is the one a host uses.
/// </para>
/// <para>
/// It never fails a send: a broadcast failure is a different rule from the ones the suite is
/// about, and a flaky port would make every card assertion mean two things at once.
/// </para>
/// </remarks>
internal sealed class RecordingTransport : IStreamingTransport
{
    private readonly List<Broadcast> _broadcasts = [];

    /// <summary>Everything the gate has pushed, in order.</summary>
    public IReadOnlyList<Broadcast> Broadcasts => _broadcasts;

    /// <summary>The Evidence Cards, in the order they were broadcast.</summary>
    public IReadOnlyList<EvidenceCardRequest> Cards =>
        _broadcasts.Where(b => b.Event == TransportEvent.EvidenceCardRequest)
            .Select(b => (EvidenceCardRequest)b.Payload)
            .ToArray();

    /// <summary>The card most recently broadcast for one entry, or null if none was.</summary>
    public EvidenceCardRequest? CardFor(Guid entryId) =>
        Cards.LastOrDefault(c => c.DocketId == entryId);

    /// <summary>Forget everything broadcast so far — used to scope a rehydration to its own step.</summary>
    public int Mark() => _broadcasts.Count;

    /// <summary>Everything broadcast since a mark.</summary>
    public IReadOnlyList<Broadcast> Since(int mark) => _broadcasts.Skip(mark).ToArray();

    public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
    {
        _broadcasts.Add(new Broadcast(connectionId, eventType, payload));
        return Task.CompletedTask;
    }

    public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
    {
        _broadcasts.Add(new Broadcast(groupId, eventType, payload));
        return Task.CompletedTask;
    }

    public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(string sessionGroupId, Guid docketId, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "The conformance driver never blocks on a reviewer: a fixture's decide step goes through HandleDecisionAsync.");

    public bool TryDeliverResponse(Guid docketId, EvidenceCardResponse response) => false;

    /// <summary>One push: the group or connection it went to, what kind it was, and the payload.</summary>
    internal sealed record Broadcast(string Target, TransportEvent Event, object Payload);
}
