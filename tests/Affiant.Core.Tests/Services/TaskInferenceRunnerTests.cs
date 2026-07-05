namespace Affiant.Core.Tests.Services;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TaskInferenceRunnerTests
{
    // --- Fakes ---

    private sealed class FakePort : IInferenceCompletionPort
    {
        private readonly Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> _impl;

        public FakePort(Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> impl)
            => _impl = impl;

        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request,
            CancellationToken cancellationToken = default)
            => _impl(request, cancellationToken);
    }

    private static FakePort PortReturning(JsonElement json)
        => new((_, _) => Task.FromResult(json));

    private static FakePort PortThrowing(Exception ex)
        => new((_, _) => throw ex);

    private sealed class ThreeFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Title", "string", "The title"),
            new("Priority", "string", "Priority level"),
            new("Status", "string", "Current status"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private static (TaskInferenceRunner runner, ContextFabric fabric) BuildRunner(FakePort port)
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var runner = new TaskInferenceRunner(port, fabric, step, NullLogger<TaskInferenceRunner>.Instance);
        return (runner, fabric);
    }

    // --- Test 1: happy path ---

    [Fact]
    public async Task RunAsync_PortReturnsValidJson_MergesAndReturnsResult()
    {
        var json = JsonDocument.Parse("""
            {
                "Title": { "value": "Fix bug", "confidence": 0.9 },
                "Priority": { "value": "High", "confidence": 0.8 }
            }
            """).RootElement;

        var (runner, fabric) = BuildRunner(PortReturning(json));

        var result = await runner.RunAsync(
            new ThreeFieldStrategy(),
            Array.Empty<AffiantChatMessage>(),
            "CreateThing",
            new Dictionary<string, object?>());

        Assert.Equal(3, result.TotalFieldsInSchema);
        Assert.True(result.MergedFields.ContainsKey("Title"));
        Assert.True(result.MergedFields.ContainsKey("Priority"));
    }

    // --- Test 2: port throws non-cancellation exception → fail-safe ---

    [Fact]
    public async Task RunAsync_PortThrowsNonCancellation_LogsWarningAndReturnsEmptyResult()
    {
        var (runner, _) = BuildRunner(PortThrowing(new InvalidOperationException("port failure")));

        var result = await runner.RunAsync(
            new ThreeFieldStrategy(),
            Array.Empty<AffiantChatMessage>(),
            "CreateThing",
            new Dictionary<string, object?>());

        Assert.Equal(3, result.TotalFieldsInSchema);
        Assert.Equal(0, result.FieldsInLlmResponse);
        Assert.Empty(result.MergedFields);
    }

    // --- Test 3: OperationCanceledException re-throws ---

    [Fact]
    public async Task RunAsync_PortThrowsCancellation_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (runner, _) = BuildRunner(PortThrowing(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                new ThreeFieldStrategy(),
                Array.Empty<AffiantChatMessage>(),
                "CreateThing",
                new Dictionary<string, object?>(),
                cts.Token));
    }

    // --- Test 4: JsonException from port → fail-safe ---

    [Fact]
    public async Task RunAsync_PortThrowsJsonException_LogsWarningAndReturnsEmptyResult()
    {
        var (runner, _) = BuildRunner(PortThrowing(new JsonException("bad json from port")));

        var result = await runner.RunAsync(
            new ThreeFieldStrategy(),
            Array.Empty<AffiantChatMessage>(),
            "CreateThing",
            new Dictionary<string, object?>());

        Assert.Equal(3, result.TotalFieldsInSchema);
        Assert.Equal(0, result.FieldsInLlmResponse);
        Assert.Empty(result.MergedFields);
    }

    // --- Test 5: constructor null guards ---

    [Fact]
    public void Constructor_NullPort_Throws()
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);

        Assert.Throws<ArgumentNullException>(() =>
            new TaskInferenceRunner(null!, fabric, step, NullLogger<TaskInferenceRunner>.Instance));
    }

    [Fact]
    public void Constructor_NullFabric_Throws()
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var port = PortReturning(default);

        Assert.Throws<ArgumentNullException>(() =>
            new TaskInferenceRunner(port, null!, step, NullLogger<TaskInferenceRunner>.Instance));
    }
}
