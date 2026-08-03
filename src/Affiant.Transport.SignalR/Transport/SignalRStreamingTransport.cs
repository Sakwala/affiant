namespace Affiant.Transport.SignalR.Transport;

using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Hubs;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Singleton SignalR implementation of <see cref="IStreamingTransport"/>. Wraps
/// <see cref="IHubContext{THub}"/> for broadcast and maintains an in-process TCS registry
/// so <see cref="AwaitEvidenceCardResponseAsync"/> can be unblocked by a later
/// <see cref="TryDeliverResponse"/> call from any hub instance.
/// </summary>
public sealed class SignalRStreamingTransport<THub>(IHubContext<THub> hubContext) : IStreamingTransport
    where THub : AffiantHub
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<EvidenceCardResponse>> _pending = new();

    public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
        => hubContext.Clients.Client(connectionId).SendAsync(eventType.ToClientEventName(), payload, ct);

    public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
        => hubContext.Clients.Group(groupId).SendAsync(eventType.ToClientEventName(), payload, ct);

    /// <summary>Document-reserved (P1a) — see <see cref="IStreamingTransport.AwaitEvidenceCardResponseAsync"/>'s docs.</summary>
    public async Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
        string sessionGroupId, Guid docketId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<EvidenceCardResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(docketId, tcs))
        {
            // Duplicate FileReviewAsync racing — reuse the existing TCS.
            _pending.TryGetValue(docketId, out var existing);
            tcs = existing ?? tcs;
        }

        ct.Register(() =>
        {
            if (_pending.TryRemove(docketId, out var toCancel))
                toCancel.TrySetCanceled(ct);
        });

        try
        {
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _pending.TryRemove(docketId, out _);
        }
    }

    /// <summary>
    /// Routes a reviewer decision to the <see cref="AwaitEvidenceCardResponseAsync"/> call blocking
    /// on <paramref name="docketId"/>. Returns <c>true</c> if a live waiter was found;
    /// <c>false</c> if the host was restarted and the docket-replay path must be used.
    /// </summary>
    public bool TryDeliverResponse(Guid docketId, EvidenceCardResponse response)
    {
        if (_pending.TryRemove(docketId, out var tcs))
        {
            tcs.TrySetResult(response);
            return true;
        }
        return false;
    }
}

internal static class TransportEventExtensions
{
    internal static string ToClientEventName(this TransportEvent evt) => evt switch
    {
        TransportEvent.EvidenceCardRequest => "ConfirmAction",
        TransportEvent.AgentMessage        => "ReceiveToken",
        TransportEvent.ContextUpdate       => "ContextUpdated",
        TransportEvent.SystemNotification  => "SystemNotification",
        _                                  => evt.ToString()
    };
}
