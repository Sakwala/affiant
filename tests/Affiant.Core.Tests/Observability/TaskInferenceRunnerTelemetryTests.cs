namespace Affiant.Core.Tests.Observability;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Verifies that TaskInferenceRunner emits the correct OTel span events on Activity.Current:
/// inference.completed (success) and inference.failed (all exception kinds, including
/// OperationCanceledException which is re-thrown after emission).
/// Events are captured by inspecting the root test span started before each test.
/// </summary>
public class TaskInferenceRunnerTelemetryTests
{
    // ── Listener helpers ──────────────────────────────────────────────────────

    private static (ActivityListener Listener, Activity? Root) StartListening()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name is "Affiant.Framework" or "Affiant.TaskInference",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var root = AffiantTelemetry.AffiantActivitySource.StartActivity("test_root");
        return (listener, root);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulMerge_EmitsInferenceCompleted_WithThreeAttributes()
    {
        var (listener, root) = StartListening();

        try
        {
            var json = JsonDocument.Parse("""
                {
                    "Title": { "value": "Fix bug", "confidence": 0.9 },
                    "Priority": { "value": "High", "confidence": 0.8 }
                }
                """).RootElement;
            var (runner, _) = BuildRunner(new FakePort((_, _) => Task.FromResult(json)));

            await runner.RunAsync(
                new ThreeFieldStrategy(),
                new ChatHistory(),
                "CreateThing",
                new Dictionary<string, object?>());
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "inference.completed"),
            "Expected inference.completed event to be emitted");
        var completed = events.Single(e => e.Name == "inference.completed");
        var tags = completed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        // Two fields came back; all three attribute keys must be present.
        Assert.True(tags.ContainsKey("affiant.fields.merged"), "affiant.fields.merged tag missing");
        Assert.True(tags.ContainsKey("affiant.fields.in_response"), "affiant.fields.in_response tag missing");
        Assert.True(tags.ContainsKey("affiant.fields.in_schema"), "affiant.fields.in_schema tag missing");

        Assert.Equal(3, Convert.ToInt32(tags["affiant.fields.in_schema"]));   // strategy has 3 fields
        Assert.Equal(2, Convert.ToInt32(tags["affiant.fields.in_response"]));  // 2 fields in LLM JSON
    }

    [Fact]
    public async Task PortThrowsGenericException_EmitsInferenceFailedWithProviderOutage()
    {
        var (listener, root) = StartListening();

        try
        {
            var (runner, _) = BuildRunner(new FakePort((_, _) =>
                throw new InvalidOperationException("provider outage")));

            await runner.RunAsync(
                new ThreeFieldStrategy(),
                new ChatHistory(),
                "CreateThing",
                new Dictionary<string, object?>());
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "inference.failed"),
            "Expected inference.failed event to be emitted");
        var failed = events.Single(e => e.Name == "inference.failed");
        var tags = failed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("CreateThing", tags["affiant.function.name"]?.ToString());
        Assert.Equal("provider_outage", tags["affiant.error.kind"]?.ToString());
    }

    [Fact]
    public async Task PortThrowsJsonException_EmitsInferenceFailedWithJsonParse()
    {
        var (listener, root) = StartListening();

        try
        {
            var (runner, _) = BuildRunner(new FakePort((_, _) =>
                throw new JsonException("malformed json")));

            await runner.RunAsync(
                new ThreeFieldStrategy(),
                new ChatHistory(),
                "CreateThing",
                new Dictionary<string, object?>());
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "inference.failed"),
            "Expected inference.failed event to be emitted");
        var failed = events.Single(e => e.Name == "inference.failed");
        var tags = failed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("CreateThing", tags["affiant.function.name"]?.ToString());
        Assert.Equal("json_parse", tags["affiant.error.kind"]?.ToString());
    }

    [Fact]
    public async Task PortThrowsOperationCanceled_EmitsInferenceFailedWithCancelled_AndRethrows()
    {
        var (listener, root) = StartListening();
        bool exceptionRethrown = false;

        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var (runner, _) = BuildRunner(new FakePort((_, _) =>
                throw new OperationCanceledException(cts.Token)));

            try
            {
                await runner.RunAsync(
                    new ThreeFieldStrategy(),
                    new ChatHistory(),
                    "CreateThing",
                    new Dictionary<string, object?>(),
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                exceptionRethrown = true;
            }
        }
        finally
        {
            root?.Dispose();
            listener.Dispose();
        }

        Assert.True(exceptionRethrown,
            "OperationCanceledException must be re-thrown after telemetry emission");

        var events = root?.Events.ToList() ?? [];
        Assert.True(events.Any(e => e.Name == "inference.failed"),
            "Expected inference.failed event to be emitted before re-throw");
        var failed = events.Single(e => e.Name == "inference.failed");
        var tags = failed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("CreateThing", tags["affiant.function.name"]?.ToString());
        Assert.Equal("cancelled", tags["affiant.error.kind"]?.ToString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (TaskInferenceRunner Runner, ContextFabric Fabric) BuildRunner(FakePort port)
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var runner = new TaskInferenceRunner(port, fabric, step, NullLogger<TaskInferenceRunner>.Instance);
        return (runner, fabric);
    }

    private sealed class FakePort(
        Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> impl)
        : IInferenceCompletionPort
    {
        public Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request, CancellationToken cancellationToken = default)
            => impl(request, cancellationToken);
    }

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
}
