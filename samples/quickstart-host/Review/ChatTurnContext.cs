namespace QuickstartHost.Review;

/// <summary>
/// The identity of the turn currently running, scoped to one hub invocation.
///
/// <para>
/// It exists because a chat turn does not run inside an HTTP request. A SignalR hub method runs
/// on an already-established connection, so <c>IHttpContextAccessor.HttpContext</c> is null for
/// the whole turn — including when the framework's review filter asks
/// <see cref="HttpReviewContextProvider"/> who is proposing this write. The hub fills this in
/// before it invokes the model, and the provider reads it.
/// </para>
///
/// <para>
/// Registered scoped. SignalR creates one dependency-injection scope per hub method invocation, so
/// one instance serves one turn, and the kernel the hub resolves from that same scope hands the
/// framework's filters the same instance.
/// </para>
/// </summary>
public sealed class ChatTurnContext
{
    /// <summary>The session (and SignalR group) the turn belongs to. Empty until the hub sets it.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Who is holding the conversation. This sample has no sign-in; see <see cref="HttpReviewContextProvider"/>.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>True once the hub has populated this instance for a turn.</summary>
    public bool IsSet => !string.IsNullOrEmpty(SessionId);
}
