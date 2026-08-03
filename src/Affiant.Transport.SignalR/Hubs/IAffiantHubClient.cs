namespace Affiant.Transport.SignalR.Hubs;

using Affiant.Abstractions.Transport;

/// <summary>
/// P4 (area-4, ruled 2026-08-04): strongly-typed client-proxy contract for <see cref="AffiantHub"/>
/// (<c>Hub&lt;IAffiantHubClient&gt;</c>). Method names are locked to
/// <see cref="Transport.TransportEventExtensions.ToClientEventName"/>'s output strings — one method
/// per <see cref="TransportEvent"/> member, same order — so a hub subclass calling
/// <c>Clients.Caller.ReceiveToken(chunk)</c> gets compile-time name AND argument-type checking
/// instead of the raw, error-prone <c>Clients.Caller.SendAsync("ReceiveToken", chunk)</c> string
/// literal both reference hosts' hot-path token-streaming code used exclusively before this change
/// (area-4 d1-fw-intent.md finding D — the framework's own hub base didn't route through its own
/// enum either, the strongest evidence the bypass was a shape gap, not host laziness).
///
/// <para>
/// <b>Scope: this is a C#-side compile-time safety net only — it does not generate or constrain
/// TypeScript.</b> The wire method NAME each member below produces is unchanged from the untyped
/// path (<see cref="Transport.TransportEventExtensions.ToClientEventName"/> is still what
/// <see cref="Transport.SignalRStreamingTransport{THub}"/> uses for framework-owned broadcasts
/// through <c>IStreamingTransport</c>, which stays deliberately untyped — see that interface's own
/// remarks for why: framework services outside a hub's own class have no <c>Clients</c> to be typed
/// against, and payload types genuinely vary by call site for events with no dedicated payload
/// record). This interface only changes what's available INSIDE a hub subclass's own methods via
/// <c>Clients.Caller</c>/<c>Clients.Group(...)</c>/etc.
/// </para>
///
/// <para>
/// <b>Two members carry no dedicated payload record anywhere in the framework</b>
/// (<see cref="ReceiveToken"/> for <c>AgentMessage</c>, <see cref="ContextUpdated"/> for
/// <c>ContextUpdate</c> — confirmed by the area-4 fw-wire-census pack) — their parameter stays
/// <c>object</c>, matching <c>IStreamingTransport.SendAsync</c>'s own genericness for these two
/// events rather than inventing a payload shape the framework doesn't otherwise define.
/// </para>
/// </summary>
public interface IAffiantHubClient
{
    Task ConfirmAction(EvidenceCardRequest payload);

    /// <summary>
    /// Document-reserved alongside <see cref="TransportEvent.EvidenceCardResponse"/> — see that
    /// member's docs. No framework production code broadcasts this; the reviewer's decision travels
    /// UI→server via a host hub RPC method instead, never this direction.
    /// </summary>
    Task EvidenceCardResponse(EvidenceCardResponse payload);

    Task ReceiveToken(object payload);

    Task ContextUpdated(object payload);

    Task SystemNotification(SystemNotificationPayload payload);

    Task DocketExpiring(DocketExpiringNotification payload);

    Task DocketExpired(DocketExpiredNotification payload);

    Task GuideUI(UiGuidancePayload payload);
}
