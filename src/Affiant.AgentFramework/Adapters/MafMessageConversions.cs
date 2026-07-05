namespace Affiant.AgentFramework.Adapters;

using Affiant.Abstractions.Models;
using Microsoft.Extensions.AI;

/// <summary>
/// Converts between Microsoft.Extensions.AI's <see cref="ChatMessage"/> and the backend-neutral
/// <see cref="AffiantChatMessage"/> at the MAF edge. Inference consumes only role and content;
/// this conversion preserves those plus the neutral author identifier.
/// </summary>
internal static class MafMessageConversions
{
    public static IReadOnlyList<AffiantChatMessage> ToNeutral(IEnumerable<ChatMessage> messages)
    {
        var result = new List<AffiantChatMessage>();
        foreach (var message in messages)
        {
            result.Add(new AffiantChatMessage(message.Role.Value, message.Text ?? string.Empty)
            {
                AuthorName = message.AuthorName,
            });
        }

        return result;
    }

    public static List<ChatMessage> ToChatMessages(IReadOnlyList<AffiantChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            result.Add(new ChatMessage(new ChatRole(message.Role), message.Content)
            {
                AuthorName = message.AuthorName,
            });
        }

        return result;
    }
}
