namespace Affiant.SemanticKernel.Filters;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// Converts between Semantic Kernel's <see cref="ChatMessageContent"/> and the backend-neutral
/// <see cref="AffiantChatMessage"/> at the SK edge. Inference consumes only role and content, but
/// the conversion also round-trips a single tool-call or tool-result turn through
/// <see cref="AffiantChatMessage"/>'s optional <c>ToolCallId</c>/<c>FunctionName</c>/<c>ArgumentsJson</c>
/// fields (the R2 no-data-loss invariant), so <c>SessionRehydrator</c> can reconstruct SK's
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> turns on reconnect.
/// </summary>
internal static class SkMessageConversions
{
    public static IReadOnlyList<AffiantChatMessage> ToNeutral(ChatHistory history)
    {
        var result = new List<AffiantChatMessage>(history.Count);
        foreach (var message in history)
        {
            string? toolCallId = null;
            string? functionName = null;
            string? argumentsJson = null;
            var content = message.Content ?? string.Empty;

            var call = message.Items.OfType<FunctionCallContent>().FirstOrDefault();
            if (call is not null)
            {
                toolCallId = call.Id;
                functionName = call.FunctionName;
                argumentsJson = SerializeArguments(call.Arguments);
            }

            var toolResult = message.Items.OfType<FunctionResultContent>().FirstOrDefault();
            if (toolResult is not null)
            {
                toolCallId = toolResult.CallId;
                functionName ??= toolResult.FunctionName;
                if (string.IsNullOrEmpty(content))
                    content = toolResult.Result?.ToString() ?? string.Empty;
            }

            result.Add(new AffiantChatMessage(message.Role.Label, content)
            {
                AuthorName = message.AuthorName,
                ModelId = message.ModelId,
                ToolCallId = toolCallId,
                FunctionName = functionName,
                ArgumentsJson = argumentsJson,
            });
        }

        return result;
    }

    public static ChatHistory ToChatHistory(IReadOnlyList<AffiantChatMessage> messages)
    {
        var history = new ChatHistory();
        foreach (var message in messages)
        {
            var role = new AuthorRole(message.Role);
            var content = new ChatMessageContent(role, message.Content)
            {
                AuthorName = message.AuthorName,
                ModelId = message.ModelId,
            };

            if (role == AuthorRole.Tool && message.ToolCallId is not null)
            {
                content.Items.Add(new FunctionResultContent(
                    callId: message.ToolCallId,
                    pluginName: null,
                    functionName: message.FunctionName,
                    result: message.Content));
            }
            else if (message.FunctionName is not null)
            {
                content.Items.Add(new FunctionCallContent(
                    functionName: message.FunctionName,
                    id: message.ToolCallId,
                    arguments: DeserializeArguments(message.ArgumentsJson)));
            }

            history.Add(content);
        }

        return history;
    }

    /// <summary>
    /// The conversation history a kernel carries, in neutral form — the one reading every seam that
    /// builds a <c>ToolInvocationRequest</c> uses, so the invocation stage and the completion stage
    /// of one turn cannot see two different histories (PV-3).
    /// </summary>
    /// <remarks>
    /// A kernel with nothing under <c>ChatHistory</c> yields an empty list, which the step reads as
    /// "no turn in hand": the port's own presence report then stands, unverified. Every host that
    /// wants presence established from what the person typed puts the history there, which is what
    /// the framework's own SK wiring does.
    /// </remarks>
    public static IReadOnlyList<AffiantChatMessage> HistoryOf(Kernel kernel) =>
        kernel.Data.TryGetValue("ChatHistory", out var history) && history is ChatHistory chat
            ? ToNeutral(chat)
            : [];

    private static string? SerializeArguments(KernelArguments? arguments) =>
        arguments is null
            ? null
            : JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value));

    private static KernelArguments? DeserializeArguments(string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
            return null;

        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
        return parsed is null ? null : new KernelArguments(parsed);
    }
}
