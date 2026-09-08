namespace Affiant.Core.Services;

using Affiant.Abstractions.Models;

/// <summary>
/// The one reading of "the current turn" every caller of
/// <see cref="Filters.TaskInferenceStep"/> shares, so two shipped callers cannot grade the same
/// history two ways (PV-3).
/// </summary>
internal static class ConversationTurn
{
    /// <summary>
    /// The role a person's turn carries in the neutral history. Every shipped bridge writes it:
    /// Semantic Kernel converts <c>AuthorRole.User.Label</c> and Extensions.AI (with the Agent
    /// Framework on top of it) converts <c>ChatRole.User.Value</c>, and both of those are "user".
    /// Matched case-insensitively because a host may build an <see cref="AffiantChatMessage"/> by
    /// hand.
    /// </summary>
    private const string UserRole = "user";

    /// <summary>
    /// The current turn's user text, or <see langword="null"/> when this history holds no turn of a
    /// person's at all — the one state in which the step has nothing to establish presence against.
    /// </summary>
    /// <remarks>
    /// A user message whose <see cref="AffiantChatMessage.Content"/> is <see langword="null"/> is an
    /// empty utterance, not a missing one: the person's turn is there, so the finder stays in charge
    /// and finds nothing, rather than handing the grade back to the port's own claim.
    /// </remarks>
    public static string? LatestUtterance(IReadOnlyList<AffiantChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(history[i].Role, UserRole, StringComparison.OrdinalIgnoreCase))
                return history[i].Content ?? string.Empty;
        }

        return null;
    }
}
