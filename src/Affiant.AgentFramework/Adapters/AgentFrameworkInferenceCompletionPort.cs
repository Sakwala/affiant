namespace Affiant.AgentFramework.Adapters;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// IInferenceCompletionPort backed by Microsoft.Extensions.AI's IChatClient — the MAF counterpart
/// of Affiant.SemanticKernel's SemanticKernelInferenceCompletionPort. The inference call omits
/// ChatOptions.Tools entirely so no function-calling recursion is possible: this is a plain
/// structured-completion call, never routed through FunctionInvokingChatClient's tool loop.
/// </summary>
public sealed class AgentFrameworkInferenceCompletionPort : IInferenceCompletionPort
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<AgentFrameworkInferenceCompletionPort> _logger;

    public AgentFrameworkInferenceCompletionPort(
        IChatClient chatClient,
        ILogger<AgentFrameworkInferenceCompletionPort> logger)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<JsonElement> CompleteStructuredAsync(
        InferenceCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var llmSpan = AffiantTelemetry.AffiantTaskInferenceActivitySource
            .StartActivity("inference.llm_call", ActivityKind.Client);
        llmSpan?.SetTag(L2TelemetryKeys.FunctionName, request.FunctionName);
        llmSpan?.SetTag(L2TelemetryKeys.StrategyType, request.Strategy.GetType().FullName ?? string.Empty);

        try
        {
            var messages = MafMessageConversions.ToChatMessages(request.History);

            var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
            messages.Add(new ChatMessage(ChatRole.User, BuildPrompt(request.Strategy, today)));

            // No ChatOptions.Tools => FunctionInvokingChatClient (if present in the chain) has
            // nothing to route to; the call is a plain structured completion.
            var response = await _chatClient.GetResponseAsync(messages, options: null, cancellationToken)
                .ConfigureAwait(false);

            var content = StripMarkdownFences(response.Text ?? string.Empty);

            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentFrameworkInferenceCompletionPort: inference call failed for {EntityName}/{FunctionName}",
                request.Strategy.EntityName, request.FunctionName);
            throw;
        }
    }

    private static string BuildPrompt(ITaskInferenceStrategy strategy, string today)
    {
        var sb = new StringBuilder();
        sb.Append($"Based ONLY on the conversation above, extract {strategy.EntityName} details.\n");
        sb.Append($"Today's date is {today}.\n\n");
        sb.Append("For each field, provide the extracted value and your confidence (0.0–1.0).\n");
        sb.Append("Use null for value and 0.0 for confidence for any field you cannot determine.\n");
        sb.Append("Respond with ONLY a valid JSON object — no explanation, no markdown fences, no other text.\n\n");
        sb.Append("Expected JSON structure:\n{\n");

        foreach (var field in strategy.Fields)
        {
            sb.Append($"  \"{field.Name}\": {{\"value\": \"<{field.Description}");
            if (field.Enum is { Count: > 0 })
                sb.Append($", one of: {string.Join("|", field.Enum)}");
            if (field.Pattern is not null)
                sb.Append($", pattern: {field.Pattern}");
            sb.Append($" ({field.JsonType}), or null>\", \"confidence\": 0.0}},\n");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string StripMarkdownFences(string content)
    {
        content = content.Trim();
        if (!content.StartsWith("```", StringComparison.Ordinal))
            return content;
        var firstNewline = content.IndexOf('\n');
        var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline > 0 && lastFence > firstNewline)
            content = content[(firstNewline + 1)..lastFence].Trim();
        return content;
    }
}
