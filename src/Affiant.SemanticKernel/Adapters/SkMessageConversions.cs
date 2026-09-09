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
    /// A kernel with nothing under <c>ChatHistory</c> yields an empty list. The convention is a host
    /// contract — nothing in this framework writes <c>kernel.Data["ChatHistory"]</c> — so a caller
    /// that gets no turn from here has a second reading to try before it concludes there is none:
    /// <see cref="AutoFunctionInvocationContext.ChatHistory"/>, which Semantic Kernel hands the
    /// auto-invocation bridge on every call (design record A25). Only a caller with neither reads
    /// "no turn in hand", and then the port's own presence report stands, unverified.
    /// </remarks>
    public static IReadOnlyList<AffiantChatMessage> HistoryOf(Kernel kernel) =>
        kernel.Data.TryGetValue("ChatHistory", out var history) && history is ChatHistory chat
            ? ToNeutral(chat)
            : [];

    /// <summary>
    /// Whether a neutral history carries a message in a person's role, which is the only thing the
    /// completion stage takes from it: the turn is the last user message, and a history holding none
    /// is read as no turn at all (PV-3).
    /// </summary>
    /// <remarks>
    /// The test a caller of <see cref="HistoryOf"/> applies before preferring the kernel's reading
    /// over Semantic Kernel's own. Non-empty is not the same question: a host that puts a
    /// system-message-only <see cref="ChatHistory"/> on the kernel has adopted the convention badly
    /// rather than not at all, and a caller that read that as a turn would beat the history SK hands
    /// the bridge with one that has nothing to grade against — the no-turn path A25 closes.
    /// </remarks>
    public static bool CarriesUserTurn(IReadOnlyList<AffiantChatMessage> history) =>
        // The same role and the same comparison Affiant.Core's ConversationTurn uses to pick the
        // turn out of this history; it is internal to that assembly, so the reading is restated
        // rather than called. AuthorRole.User.Label is "user".
        history.Any(m => string.Equals(m.Role, AuthorRole.User.Label, StringComparison.OrdinalIgnoreCase));

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
