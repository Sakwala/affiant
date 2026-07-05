namespace Affiant.SemanticKernel.Filters;

using Affiant.Abstractions.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// Converts between Semantic Kernel's <see cref="ChatMessageContent"/> and the backend-neutral
/// <see cref="AffiantChatMessage"/> at the SK edge. Inference consumes only role and content;
/// this conversion preserves those plus the neutral author/model identifiers.
/// </summary>
internal static class SkMessageConversions
{
    public static IReadOnlyList<AffiantChatMessage> ToNeutral(ChatHistory history)
    {
        var result = new List<AffiantChatMessage>(history.Count);
        foreach (var message in history)
        {
            result.Add(new AffiantChatMessage(message.Role.Label, message.Content ?? string.Empty)
            {
                AuthorName = message.AuthorName,
                ModelId = message.ModelId,
            });
        }

        return result;
    }

    public static ChatHistory ToChatHistory(IReadOnlyList<AffiantChatMessage> messages)
    {
        var history = new ChatHistory();
        foreach (var message in messages)
        {
            history.Add(new ChatMessageContent(new AuthorRole(message.Role), message.Content)
            {
                AuthorName = message.AuthorName,
                ModelId = message.ModelId,
            });
        }

        return history;
    }
}
