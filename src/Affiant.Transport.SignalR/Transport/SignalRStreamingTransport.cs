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

/// <summary>
/// Maps each <see cref="TransportEvent"/> member to the SignalR client method name it is dispatched
/// under. <b>P1c (area-4, ruled 2026-08-04):</b> now <c>public</c> (was <c>internal</c>) and total —
/// an explicit arm per enum member, no <c>default</c>/discard-to-<c>ToString()</c> fallthrough.
/// Before this change, 4 of 8 members silently fell through to <c>evt.ToString()</c>, meaning a
/// rename or reorder of those members silently renamed the wire method with no compiler signal, and
/// the method being <c>internal</c> meant a host's own contract net could only reach it via
/// reflection (which detects removal, not an output-string change). Both gaps are closed here:
/// adding a <see cref="TransportEvent"/> member without a matching arm is a compile error (CS8509,
/// "the switch expression does not handle all values of its input type" for the new NAMED member —
/// this package's <c>TreatWarningsAsErrors</c> turns the warning into a build failure), and the
/// method is directly callable — no reflection needed — by any code (including a host's own
/// contract tests) that references this package.
/// <para>
/// The <c>#pragma</c> below suppresses only CS8524 — a distinct, unavoidable diagnostic every
/// enum switch EXPRESSION without a discard arm triggers in C#, because enums admit any underlying
/// integral value via casting (e.g. <c>(TransportEvent)99</c>), not just their named members; no
/// finite set of named arms can ever satisfy it. Suppressing it does not weaken the guarantee this
/// class exists for — CS8509 (a genuinely missing NAMED member) remains fully active and still
/// fails the build.
/// </para>
/// </summary>
public static class TransportEventExtensions
{
#pragma warning disable CS8524 // exhaustive over every NAMED TransportEvent member (CS8509 stays live); see class remarks for why this specific diagnostic is unavoidable for any enum switch expression.
    public static string ToClientEventName(this TransportEvent evt) => evt switch
    {
        TransportEvent.EvidenceCardRequest  => "ConfirmAction",
        TransportEvent.EvidenceCardResponse => "EvidenceCardResponse",
        TransportEvent.AgentMessage         => "ReceiveToken",
        TransportEvent.ContextUpdate        => "ContextUpdated",
        TransportEvent.SystemNotification   => "SystemNotification",
        TransportEvent.DocketExpiring       => "DocketExpiring",
        TransportEvent.DocketExpired        => "DocketExpired",
        TransportEvent.UiGuidance           => "GuideUI",
    };
#pragma warning restore CS8524
}
