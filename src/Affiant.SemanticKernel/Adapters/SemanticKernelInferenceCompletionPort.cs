namespace Affiant.SemanticKernel.Adapters;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// IInferenceCompletionPort backed by SK's IChatCompletionService.
/// Wraps the kernel's chat-completion service with FunctionChoiceBehavior.None() so the
/// inference call is a plain structured-completion with no tool routing. Per PRD §3.1.
///
/// Takes IServiceProvider (not Kernel directly) to avoid the circular DI dependency:
/// filter → runner → port → Kernel → IFunctionInvocationFilter → filter.
/// In production the scoped IServiceProvider has already built Kernel; IChatCompletionService
/// is resolved from the same scope, bypassing the cycle entirely. Per PRD §3.1.
/// </summary>
public sealed class SemanticKernelInferenceCompletionPort : IInferenceCompletionPort
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SemanticKernelInferenceCompletionPort> _logger;
    private readonly TimeProvider _time;

    /// <param name="services">The provider the SK chat-completion service is resolved from.</param>
    /// <param name="logger">Logger for inference-call failures.</param>
    /// <param name="timeProvider">
    /// The clock the today's-date line of the inference prompt is read from. Defaults to
    /// <see cref="TimeProvider.System"/>; <c>AddAffiantCore</c> registers exactly that as the DI
    /// default, and a test that pins the clock gets a deterministic prompt.
    /// </param>
    public SemanticKernelInferenceCompletionPort(
        IServiceProvider services,
        ILogger<SemanticKernelInferenceCompletionPort> logger,
        TimeProvider? timeProvider = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<JsonElement> CompleteStructuredAsync(
        InferenceCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Optional span for trace-readability: distinguishes "framework inference" from
        // "raw SK chat-completion call". StartActivity returns null when no listener subscribes
        // to Affiant.TaskInference, making this a zero-cost no-op in production without OTel.
        using var llmSpan = AffiantTelemetry.AffiantTaskInferenceActivitySource
            .StartActivity("inference.llm_call", ActivityKind.Client);
        llmSpan?.SetTag(L2TelemetryKeys.FunctionName, request.FunctionName);
        llmSpan?.SetTag(L2TelemetryKeys.StrategyType, request.Strategy.GetType().FullName ?? string.Empty);

        try
        {
            var chatCompletion = _services.GetRequiredService<IChatCompletionService>();

            // Convert the neutral conversation history into an SK ChatHistory at this edge so the
            // inference call reads all context.
            var inferenceHistory = Filters.SkMessageConversions.ToChatHistory(request.History);

            var today = _time.GetUtcNow().UtcDateTime.Date.ToString("yyyy-MM-dd");
            inferenceHistory.AddUserMessage(BuildPrompt(request.Strategy, today));

            // FunctionChoiceBehavior.None() prevents tool routing during the inference call.
            // Per PRD §3.1 — the inference pass is read-only on the conversation; no tools fire.
            var settings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.None()
            };

            // Kernel is not needed when FunctionChoiceBehavior.None() is set — pass null so
            // no tool-routing cycle is triggered by the chat completion provider.
            var responses = await chatCompletion.GetChatMessageContentsAsync(
                inferenceHistory, settings, kernel: null, cancellationToken).ConfigureAwait(false);

            var content = responses.Count > 0 ? responses[0].Content ?? string.Empty : string.Empty;
            content = StripMarkdownFences(content);

            // Clone the element so the JsonDocument can be disposed.
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log and re-throw — TaskInferenceRunner owns the fail-safe; the port never swallows.
            _logger.LogWarning(ex,
                "SemanticKernelInferenceCompletionPort: inference call failed for {EntityName}/{FunctionName}",
                request.Strategy.EntityName, request.FunctionName);
            throw;
        }
    }

    /// <summary>
    /// Builds the structured-output extraction prompt from the strategy's field schema.
    /// Each field becomes one entry in the expected JSON schema; the LLM must respond with
    /// {"fieldName": {"value": "...", "confidence": 0.0–1.0}} for each field.
    /// </summary>
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
