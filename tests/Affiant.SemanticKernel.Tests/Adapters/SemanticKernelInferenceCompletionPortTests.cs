namespace Affiant.SemanticKernel.Tests.Adapters;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.SemanticKernel.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Unit tests for SemanticKernelInferenceCompletionPort.
/// Verifies prompt construction, FunctionChoiceBehavior.None() wiring, JSON parsing,
/// cancellation propagation, and exception re-throw semantics.
/// </summary>
public class SemanticKernelInferenceCompletionPortTests
{
    // ── Prompt construction ───────────────────────────────────────────────────

    [Fact]
    public async Task CompleteStructuredAsync_BuildsPromptWithStrategyFieldNames()
    {
        ChatHistory? capturedHistory = null;
        var fake = new FakeChatCompletion((history, _, _, _) =>
        {
            capturedHistory = history;
            return Task.FromResult(new ChatMessageContent(AuthorRole.Assistant,
                """{"title": {"value": "test", "confidence": 0.9}}"""));
        });

        var port = BuildPort(fake);
        var request = MakeRequest();

        await port.CompleteStructuredAsync(request);

        Assert.NotNull(capturedHistory);
        var lastMessage = capturedHistory.Last().Content ?? string.Empty;
        // Prompt must name every field from the strategy
        Assert.Contains("title", lastMessage);
        Assert.Contains("priority", lastMessage);
        // Prompt must reference the entity name
        Assert.Contains("WorkItem", lastMessage);
    }

    [Fact]
    public async Task CompleteStructuredAsync_SetsFunctionChoiceBehaviorNone()
    {
        PromptExecutionSettings? capturedSettings = null;
        var fake = new FakeChatCompletion((_, settings, _, _) =>
        {
            capturedSettings = settings;
            return Task.FromResult(new ChatMessageContent(AuthorRole.Assistant, "{}"));
        });

        var port = BuildPort(fake);
        await port.CompleteStructuredAsync(MakeRequest());

        Assert.NotNull(capturedSettings);
        Assert.NotNull(capturedSettings.FunctionChoiceBehavior);
        // FunctionChoiceBehavior.None() returns a NoneFunctionChoiceBehavior instance.
        Assert.Equal(
            FunctionChoiceBehavior.None().GetType(),
            capturedSettings.FunctionChoiceBehavior.GetType());
    }

    [Fact]
    public async Task CompleteStructuredAsync_CopiesInputHistoryIntoInferenceHistory()
    {
        ChatHistory? capturedHistory = null;
        var fake = new FakeChatCompletion((history, _, _, _) =>
        {
            capturedHistory = history;
            return Task.FromResult(new ChatMessageContent(AuthorRole.Assistant, "{}"));
        });

        var port = BuildPort(fake);
        var inputHistory = new ChatHistory();
        inputHistory.AddUserMessage("first user message");
        inputHistory.AddAssistantMessage("first assistant reply");

        await port.CompleteStructuredAsync(MakeRequest(inputHistory));

        Assert.NotNull(capturedHistory);
        // Input messages must be present before the injected extraction instruction
        Assert.Contains(capturedHistory, m => m.Content == "first user message");
        Assert.Contains(capturedHistory, m => m.Content == "first assistant reply");
        // Extraction instruction appended after the input history
        Assert.True(capturedHistory.Count > 2);
    }

    // ── JSON parsing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteStructuredAsync_ReturnsJsonElementFromResponse()
    {
        const string json = """{"title": {"value": "fix leak", "confidence": 0.85}}""";
        var fake = new FakeChatCompletion((_, _, _, _) =>
            Task.FromResult(new ChatMessageContent(AuthorRole.Assistant, json)));

        var port = BuildPort(fake);
        var result = await port.CompleteStructuredAsync(MakeRequest());

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.TryGetProperty("title", out var titleEl));
        Assert.Equal("fix leak", titleEl.GetProperty("value").GetString());
    }

    [Fact]
    public async Task CompleteStructuredAsync_StripsMarkdownFencesBeforeParsing()
    {
        var fenced = "```json\n{\"title\": {\"value\": \"ok\", \"confidence\": 0.9}}\n```";
        var fake = new FakeChatCompletion((_, _, _, _) =>
            Task.FromResult(new ChatMessageContent(AuthorRole.Assistant, fenced)));

        var port = BuildPort(fake);
        var result = await port.CompleteStructuredAsync(MakeRequest());

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.TryGetProperty("title", out _));
    }

    // ── Exception semantics ───────────────────────────────────────────────────

    [Fact]
    public async Task CompleteStructuredAsync_ThrowsOnCancellation()
    {
        var fake = new FakeChatCompletion((_, _, _, ct) =>
            Task.FromException<ChatMessageContent>(new OperationCanceledException(ct)));

        var port = BuildPort(fake);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => port.CompleteStructuredAsync(MakeRequest(), cts.Token));
    }

    [Fact]
    public async Task CompleteStructuredAsync_ReThrowsNonCancellationException()
    {
        var fake = new FakeChatCompletion((_, _, _, _) =>
            Task.FromException<ChatMessageContent>(new InvalidOperationException("boom")));

        var port = BuildPort(fake);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => port.CompleteStructuredAsync(MakeRequest()));
        Assert.Equal("boom", ex.Message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SemanticKernelInferenceCompletionPort BuildPort(IChatCompletionService fake)
    {
        // Build the kernel so the fake IChatCompletionService is registered in kernel.Services.
        // Pass kernel.Services (IServiceProvider) — not Kernel directly — to break the circular
        // DI dependency that exists in production: filter → runner → port → Kernel → filter.
        // In production the scoped IServiceProvider resolves Kernel from its scope cache (already
        // built by the time CompleteStructuredAsync is called). In tests kernel.Services suffices.
        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton(fake);
        var kernel = kernelBuilder.Build();
        return new SemanticKernelInferenceCompletionPort(
            kernel.Services, NullLogger<SemanticKernelInferenceCompletionPort>.Instance);
    }

    private static InferenceCompletionRequest MakeRequest(ChatHistory? history = null)
    {
        return new InferenceCompletionRequest(
            History: history ?? new ChatHistory(),
            Strategy: new WorkItemStrategy(),
            FunctionName: "CreateWorkItem",
            Arguments: new Dictionary<string, object?>(0));
    }

    private sealed class WorkItemStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "WorkItem";
        public IReadOnlyList<TaskInferenceField> Fields =>
        [
            new TaskInferenceField("title", "string", "Short title of the work item"),
            new TaskInferenceField("priority", "string", "Priority level",
                Enum: new[] { "Low", "Medium", "High" })
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FakeChatCompletion : IChatCompletionService
    {
        private readonly Func<ChatHistory, PromptExecutionSettings?, Kernel?, CancellationToken, Task<ChatMessageContent>> _impl;

        public FakeChatCompletion(
            Func<ChatHistory, PromptExecutionSettings?, Kernel?, CancellationToken, Task<ChatMessageContent>> impl)
            => _impl = impl;

        public IReadOnlyDictionary<string, object?> Attributes =>
            new Dictionary<string, object?>();

        public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
            => new[] { await _impl(chatHistory, executionSettings, kernel, cancellationToken) };

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Streaming not used in structured-output inference.");
    }
}
