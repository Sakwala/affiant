namespace Affiant.Transport.SignalR.Transport;

using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;
using Affiant.Transport.SignalR.Hubs;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Singleton SignalR implementation of <see cref="IStreamingTransport"/>. Wraps
/// <see cref="IHubContext{THub}"/> for broadcast and maintains an in-process TCS registry
/// so <see cref="AwaitEventAsync{T}"/> can be unblocked by a later
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

    public IAsyncEnumerable<TransportMessage> ReceiveAsync(string connectionId, CancellationToken ct)
        => throw new NotSupportedException(
            "Pull-based receive is not supported over SignalR; use AwaitEventAsync instead.");

    public async Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct = default)
    {
        if (typeof(T) != typeof(EvidenceCardResponse))
            throw new NotSupportedException(
                $"AwaitEventAsync<{typeof(T).Name}> is not supported by SignalRStreamingTransport.");

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
            return (T)(object)await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _pending.TryRemove(docketId, out _);
        }
    }

    /// <summary>
    /// Routes a reviewer decision to the <see cref="AwaitEventAsync{T}"/> call blocking on
    /// <paramref name="docketId"/>. Returns <c>true</c> if a live waiter was found;
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
