namespace Affiant.Extensions.AI.Adapters;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.AI;

/// <summary>
/// Converts between Microsoft.Extensions.AI's <see cref="ChatMessage"/> and the backend-neutral
/// <see cref="AffiantChatMessage"/>. Inference consumes only role and content, but the conversion
/// also round-trips a single tool-call or tool-result turn through
/// <see cref="AffiantChatMessage"/>'s optional <c>ToolCallId</c>/<c>FunctionName</c>/<c>ArgumentsJson</c>
/// fields (the R2 no-data-loss invariant), so a persisted session can reconstruct
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> turns.
///
/// <para>
/// Copied verbatim (namespace and type name aside) from
/// <c>src/Affiant.AgentFramework/Adapters/MafMessageConversions.cs</c> — design brief
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>, decision 3.
/// That file was already pure Microsoft.Extensions.AI code with no Microsoft.Agents.AI import; it is
/// copied rather than referenced only because this package must not depend on a sibling adapter.
/// </para>
/// </summary>
internal static class ExtensionsAIMessageConversions
{
    public static IReadOnlyList<AffiantChatMessage> ToNeutral(IEnumerable<ChatMessage> messages)
    {
        var result = new List<AffiantChatMessage>();
        foreach (var message in messages)
        {
            string? toolCallId = null;
            string? functionName = null;
            string? argumentsJson = null;
            var content = message.Text ?? string.Empty;

            var call = message.Contents.OfType<FunctionCallContent>().FirstOrDefault();
            if (call is not null)
            {
                toolCallId = call.CallId;
                functionName = call.Name;
                argumentsJson = call.Arguments is null ? null : JsonSerializer.Serialize(call.Arguments);
            }

            var toolResult = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            if (toolResult is not null)
            {
                toolCallId = toolResult.CallId;
                if (string.IsNullOrEmpty(content))
                    content = toolResult.Result?.ToString() ?? string.Empty;
            }

            result.Add(new AffiantChatMessage(message.Role.Value, content)
            {
                AuthorName = message.AuthorName,
                ToolCallId = toolCallId,
                FunctionName = functionName,
                ArgumentsJson = argumentsJson,
            });
        }

        return result;
    }

    public static List<ChatMessage> ToChatMessages(IReadOnlyList<AffiantChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            var role = new ChatRole(message.Role);

            ChatMessage converted;
            if (role == ChatRole.Tool && message.ToolCallId is not null)
            {
                converted = new ChatMessage(role,
                    [new FunctionResultContent(message.ToolCallId, message.Content)]);
            }
            else if (message.FunctionName is not null)
            {
                converted = new ChatMessage(role,
                    [new FunctionCallContent(
                        message.ToolCallId ?? string.Empty,
                        message.FunctionName,
                        DeserializeArguments(message.ArgumentsJson))]);
            }
            else
            {
                converted = new ChatMessage(role, message.Content);
            }

            converted.AuthorName = message.AuthorName;
            result.Add(converted);
        }

        return result;
    }

    private static IDictionary<string, object?>? DeserializeArguments(string? argumentsJson) =>
        string.IsNullOrEmpty(argumentsJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
}
