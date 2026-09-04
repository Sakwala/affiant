namespace QuickstartHost.Review;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// Tells the framework who is proposing a write and which session's reviewer should see it.
/// Without this registration the framework's review filter logs a debug line and skips the write
/// silently, so it is required, not optional.
///
/// <para>
/// <b>Where the identity comes from.</b> Two callers reach this type. A chat turn runs inside a
/// SignalR hub invocation, where there is no <c>HttpContext</c> at all — that identity comes from
/// <see cref="ChatTurnContext"/>, which the hub fills in before invoking the model. Anything that
/// proposes a write from a plain HTTP request uses the request instead, reading the session from
/// an <c>X-Session-Id</c> header. The hub path is checked first because it is the one a live
/// conversation actually takes.
/// </para>
///
/// <para>
/// <b>No sign-in.</b> This sample has no authentication, so the user id falls back to a fixed
/// demo value. A real host reads it from the authenticated principal and returns <c>null</c> from
/// this method for an unauthenticated caller — returning <c>null</c> is how a host says "this
/// request has no identity to file a review under", and the framework then skips filing rather
/// than inventing a reviewer.
/// </para>
/// </summary>
public sealed class HttpReviewContextProvider(
    IHttpContextAccessor httpContextAccessor,
    ChatTurnContext turnContext) : IReviewContextProvider
{
    /// <summary>The stand-in identity this sample files every review under, having no sign-in.</summary>
    public const string DemoUserId = "quickstart-reviewer";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ReviewContext? BuildReviewContext(WriteProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var affidavit = ReadAffidavit(proposal);
        if (affidavit is null)
            return null;

        var sessionId = ResolveSessionId();
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var userId = string.IsNullOrEmpty(turnContext.UserId) ? DemoUserId : turnContext.UserId;

        return new ReviewContext(
            SessionId: sessionId,
            TenantId: "default",
            UserId: userId,
            ReviewerUserId: userId,
            Affidavit: affidavit);
    }

    private string? ResolveSessionId()
    {
        if (turnContext.IsSet)
            return turnContext.SessionId;

        var http = httpContextAccessor.HttpContext;
        var header = http?.Request.Headers["X-Session-Id"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header;
    }

    /// <summary>
    /// <c>WriteProposal.Envelope</c> is declared <c>object</c>. By the time the framework's review
    /// filter has deserialized a tool's JSON result it holds a <c>JsonElement</c>, not an
    /// <c>Affidavit</c> — and it was serialized with a camelCase policy, so deserializing it needs
    /// the same policy back. Code that files a proposal in-process (the development seam) passes
    /// the affidavit object straight through, so both shapes are accepted.
    /// </summary>
    private static Affidavit? ReadAffidavit(WriteProposal proposal) => proposal.Envelope switch
    {
        Affidavit affidavit => affidavit,
        JsonElement json => json.Deserialize<Affidavit>(JsonOptions),
        _ => null,
    };
}
