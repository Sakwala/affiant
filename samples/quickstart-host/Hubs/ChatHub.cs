namespace QuickstartHost.Hubs;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Services;
using Affiant.Transport.SignalR.Hubs;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using QuickstartHost.Review;

/// <summary>
/// The host's own hub: where a reviewer's decision enters the framework, and where an approved
/// affidavit reaches the database.
///
/// <para>
/// Nothing in the framework executes a write for you. The review filter files the proposal and
/// ends the model's turn; the decision travels back on a separate hub call, and
/// <see cref="ApproveEntry"/> is the one place this sample turns an approval into a row. That
/// boundary is the whole point of the design — see <see cref="Execution.LeaveWriteExecutor"/>.
/// </para>
///
/// <para>
/// <b>Why the decision is a hub call and not a return value.</b> Filing is non-blocking: the
/// framework's review filter files the entry, broadcasts the Evidence Card and returns
/// immediately. There is therefore never a call waiting inside the framework for the reviewer, so
/// <c>ReviewGate.HandleDecisionAsync</c> always returns the outcome to this hub method, which acks
/// it to the client. A blocking design would deadlock: SignalR allows one invocation per client at
/// a time by default, so the call carrying the decision would queue behind the call waiting for it.
/// </para>
/// </summary>
public sealed class ChatHub(
    IChatSessionStore chatSessionStore,
    IStreamingTransport transport,
    ReviewGate reviewGate,
    IDocketStore docketStore,
    IWriteExecutor writeExecutor,
    ChatTurnContext turnContext,
    Kernel kernel,
    ILogger<ChatHub> logger) : AffiantHub(chatSessionStore, transport)
{
    private const string TenantId = "default";

    /// <summary>
    /// Joins this connection to a session's group and hands back its transcript. Pass the id from
    /// a previous visit to rejoin it, or nothing to start a new one; the id actually joined is
    /// returned, so a client that offers a session the server no longer has still ends up in a
    /// working one.
    ///
    /// Rejoining also re-broadcasts every Evidence Card still awaiting a decision in that session,
    /// so a reviewer who reloaded the page gets their pending cards back immediately rather than
    /// waiting for the framework's next expiry sweep to redeliver them.
    /// </summary>
    public async Task<SessionJoined> RehydrateSession(string? sessionId)
    {
        var session = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : await ChatSessionStore.GetAsync(sessionId, Context.ConnectionAborted);

        session ??= await ChatSessionStore.CreateAsync(
            TenantId, HttpReviewContextProvider.DemoUserId, Context.ConnectionAborted);

        var messages = await RehydrateSessionAsync(session.SessionId, Context.ConnectionAborted);
        await reviewGate.RebroadcastPendingCardsAsync(session.SessionId, Context.ConnectionAborted);

        return new SessionJoined(session.SessionId, messages);
    }

    /// <summary>
    /// Delivers a reviewer's approval, with any fields they amended, and — only if the framework
    /// says the entry actually reached <c>Approved</c> — executes the write.
    ///
    /// A decision that arrives after the entry's deadline is answered <c>expired</c> and writes
    /// nothing, but the reviewer's amendments are still preserved on the entry so a resubmission
    /// can carry them forward. That is the framework's behaviour, not this hub's: the check for it
    /// is the returned outcome, never a client-side guess.
    /// </summary>
    public async Task<DecisionAck> ApproveEntry(Guid entryId, Dictionary<string, object?>? amendments)
    {
        var (outcome, _) = await reviewGate.HandleDecisionAsync(
            entryId, ApprovalDecision.Approved, amendments, Context.ConnectionAborted);

        if (outcome is not ReviewOutcome.Approved)
            return DecisionAck.From(entryId, outcome);

        var entry = await docketStore.GetDocketEntryAsync(entryId, Context.ConnectionAborted);
        if (entry is { Status: ReviewStatus.Approved })
        {
            var recordId = await writeExecutor.ExecuteAsync(
                entry.Envelope, entry.Amendments, Context.ConnectionAborted);
            logger.LogInformation(
                "Approved DocketEntry {EntryId} wrote leave request {RecordId}", entryId, recordId);
        }

        return DecisionAck.From(entryId, outcome);
    }

    /// <summary>Delivers a reviewer's rejection. No write happens on this path, ever.</summary>
    public async Task<DecisionAck> RejectEntry(Guid entryId)
    {
        var (outcome, _) = await reviewGate.HandleDecisionAsync(
            entryId, ApprovalDecision.Rejected, amendments: null, Context.ConnectionAborted);
        return DecisionAck.From(entryId, outcome);
    }

    /// <summary>
    /// Files a fresh review for an entry that expired unreviewed. The framework mints a new entry
    /// cloning the expired one's affidavit and broadcasts its card carrying whatever the first
    /// reviewer had already amended, so the second reviewer sees the work that was done before the
    /// window lapsed.
    /// </summary>
    public async Task<DecisionAck> ResubmitEntry(Guid entryId)
    {
        var filing = await reviewGate.ResubmitAsync(entryId, Context.ConnectionAborted);
        return filing switch
        {
            ReviewFilingResult.RequiresReview requires => new DecisionAck(
                requires.EntryId.ToString(), "pending", AmendmentsPreserved: false),
            ReviewFilingResult.Decided decided => DecisionAck.From(decided.Outcome.DocketId, decided.Outcome),
            _ => new DecisionAck(entryId.ToString(), "unknown", AmendmentsPreserved: false),
        };
    }

    /// <summary>
    /// Runs one model turn. Everything Affiant does happens inside this call: the model picks a
    /// tool, the tool returns a proposal instead of writing, the framework's filters file it for
    /// review and end the turn.
    ///
    /// With no model key configured the host says so rather than failing quietly — the review
    /// mechanics are still reachable through the development seam.
    /// </summary>
    public async Task SendMessage(string message, string sessionId)
    {
        turnContext.SessionId = sessionId;
        turnContext.UserId = HttpReviewContextProvider.DemoUserId;

        var chat = kernel.Services.GetService<IChatCompletionService>();
        if (chat is null)
        {
            await BroadcastToSessionAsync(
                sessionId,
                TransportEvent.SystemNotification,
                new SystemNotificationPayload(
                    "warning",
                    "No model is configured, so the chat path is off. Set OPENAI_API_KEY and restart, " +
                    "or file a card through the development seam — the review mechanics are the same."),
                Context.ConnectionAborted);
            return;
        }

        using var turn = BeginAgentTurn(sessionId, message);

        var stored = await ChatSessionStore.LoadMessagesAsync(sessionId, Context.ConnectionAborted);
        var history = new ChatHistory("You help staff file and amend leave requests. " +
            "Use the tools; never claim a request was saved — a human approves every write.");
        foreach (var stored_message in stored)
            history.AddMessage(new AuthorRole(stored_message.Role), stored_message.Content);
        history.AddUserMessage(message);

        var settings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        };

        var reply = await chat.GetChatMessageContentAsync(history, settings, kernel, Context.ConnectionAborted);
        var text = reply.Content ?? string.Empty;

        await ChatSessionStore.AppendMessagesAsync(
            sessionId,
            [new AffiantChatMessage("user", message), new AffiantChatMessage("assistant", text)],
            Context.ConnectionAborted);

        await BroadcastToSessionAsync(
            sessionId, TransportEvent.AgentMessage, new AgentMessagePayload(text), Context.ConnectionAborted);
    }
}

/// <summary>What <see cref="ChatHub.RehydrateSession"/> hands a client that has just joined.</summary>
public sealed record SessionJoined(string SessionId, IReadOnlyList<AffiantChatMessage> Messages);

/// <summary>
/// The server's answer to a reviewer's decision. The client renders terminal state from this and
/// from the framework's own docket broadcasts — never optimistically from the click.
/// </summary>
public sealed record DecisionAck(string EntryId, string Outcome, bool AmendmentsPreserved)
{
    internal static DecisionAck From(Guid entryId, ReviewOutcome? outcome) => outcome switch
    {
        ReviewOutcome.Approved => new DecisionAck(entryId.ToString(), "approved", false),
        ReviewOutcome.Rejected => new DecisionAck(entryId.ToString(), "rejected", false),
        ReviewOutcome.Expired expired => new DecisionAck(entryId.ToString(), "expired", expired.AmendmentsPreserved),
        ReviewOutcome.Referral => new DecisionAck(entryId.ToString(), "referred", false),
        // Null means a call inside the framework is awaiting the decision and owns the outcome.
        // This host never files that way, so it cannot happen here.
        _ => new DecisionAck(entryId.ToString(), "pending", false),
    };
}

/// <summary>The shape this sample sends for an agent message; the framework leaves it to the host.</summary>
public sealed record AgentMessagePayload(string Text);
