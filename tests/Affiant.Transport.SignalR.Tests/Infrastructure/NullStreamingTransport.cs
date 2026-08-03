namespace Affiant.Transport.SignalR.Tests.Infrastructure;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Transport;

/// <summary>
/// No-op IStreamingTransport for wiring AffiantHub in tests that don't exercise broadcast behavior.
/// </summary>
internal sealed class NullStreamingTransport : IStreamingTransport
{
    public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct) =>
        Task.CompletedTask;

    public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct) =>
        Task.CompletedTask;

    public async IAsyncEnumerable<TransportMessage> ReceiveAsync(
        string connectionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<T> AwaitEventAsync<T>(string sessionGroupId, Guid docketId, CancellationToken ct = default) =>
        Task.FromCanceled<T>(ct);
}
