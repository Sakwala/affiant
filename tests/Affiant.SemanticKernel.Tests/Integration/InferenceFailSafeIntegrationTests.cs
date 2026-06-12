namespace Affiant.SemanticKernel.Tests.Integration;

using System.Text.Json;
using Xunit;

using static IntegrationTestPipelineFactory;

/// <summary>
/// Integration tests closing L2 AC #13: verifies that InferenceTriggerFilter's fail-safe
/// correctly handles every failure mode of IInferenceCompletionPort.
///
/// Tests drive kernel.InvokeAsync (not a synthesized context) so the full filter chain
/// fires. OTel events are captured via InMemoryExporterHelper registered in the pipeline.
///
/// Failure mode taxonomy (per TaskInferenceRunner's catch ordering):
///   OperationCanceledException → inference.failed(cancelled) + re-throw
///   JsonException               → inference.failed(json_parse) + return empty result
///   Other Exception             → inference.failed(provider_outage) + return empty result
///   Non-object JSON (InvalidOperationException from TryGetProperty) → provider_outage
///   Valid JSON, no matching fields → inference.completed(fields_merged=0)
/// </summary>
[Collection("AffiantL2IntegrationTests")]
public class InferenceFailSafeIntegrationTests
{
    [Fact]
    public async Task PortThrowsProviderException_InferenceFailedEmitted_TurnCompletesWithoutPropagation()
    {
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => throw new HttpRequestException("simulated provider outage"));

        // The framework's fail-safe swallows the inference failure; the tool result propagates.
        var result = await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        Assert.NotNull(result);  // turn completes normally
        var events = exporter.ExportedActivities.SelectMany(a => a.Events).ToList();
        Assert.True(events.Any(e => e.Name == "inference.failed"),
            "Expected inference.failed event to be emitted");
        var failed = events.Single(e => e.Name == "inference.failed");
        var tags = failed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("provider_outage", tags["affiant.error.kind"]?.ToString());
        Assert.Equal("CreateThing", tags["affiant.function.name"]?.ToString());
    }

    [Fact]
    public async Task PortThrowsJsonException_InferenceFailedWithJsonParseKind_TurnCompletes()
    {
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => throw new JsonException("malformed JSON from provider"));

        var result = await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        Assert.NotNull(result);
        var events = exporter.ExportedActivities.SelectMany(a => a.Events).ToList();
        Assert.True(events.Any(e => e.Name == "inference.failed"),
            "Expected inference.failed event to be emitted");
        var failed = events.Single(e => e.Name == "inference.failed");
        var tags = failed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);
        // TaskInferenceRunner catches JsonException specifically → json_parse kind
        Assert.Equal("json_parse", tags["affiant.error.kind"]?.ToString());
    }

    [Fact]
    public async Task PortReturnsMalformedJson_InferenceFailedWithProviderOutageKind_TurnCompletes()
    {
        // Per Gotcha 1: when the port returns a non-Object JSON element (e.g. a bare string),
        // TaskInferenceStep.ExecuteAsync calls TryGetProperty on a non-Object JsonElement which
        // throws InvalidOperationException. TaskInferenceRunner catches this via the general
        // Exception branch (not the JsonException branch), so the emitted kind is provider_outage.
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => Task.FromResult(JsonDocument.Parse("\"not an object\"").RootElement.Clone()));

        var result = await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        Assert.NotNull(result);
        var events = exporter.ExportedActivities.SelectMany(a => a.Events).ToList();
        Assert.True(events.Any(e => e.Name == "inference.failed"),
            "Expected inference.failed event to be emitted");
        var failed = events.Single(e => e.Name == "inference.failed");
        var tags = failed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);
        // InvalidOperationException from TryGetProperty on a non-Object element is caught
        // by the general catch(Exception) branch → provider_outage (NOT json_parse)
        Assert.Equal("provider_outage", tags["affiant.error.kind"]?.ToString());
    }

    [Fact]
    public async Task PortReturnsObjectWithoutValidFieldStructure_InferenceCompletesWithZeroMerged()
    {
        // A valid JSON object with no property matching FakeThingStrategy.Fields ("Title") →
        // silent skip: no JsonException, merge returns fields_merged=0.
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => Task.FromResult(JsonDocument.Parse("""{"irrelevant":"data"}""").RootElement.Clone()));

        var result = await kernel.InvokeAsync("ThingPlugin", "CreateThing");

        Assert.NotNull(result);
        var events = exporter.ExportedActivities.SelectMany(a => a.Events).ToList();
        Assert.True(events.Any(e => e.Name == "inference.completed"),
            "Expected inference.completed event to be emitted");
        var completed = events.Single(e => e.Name == "inference.completed");
        var tags = completed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(0, Convert.ToInt32(tags["affiant.fields.merged"]));
    }

    [Fact]
    public async Task PortThrowsCancelled_InferenceFailedEmittedWithCancelledKind_AndPropagates()
    {
        // The port throws OperationCanceledException explicitly.
        // Per 16.2's runner contract: inference.failed(cancelled) is emitted, then re-thrown.
        // InferenceTriggerFilter re-throws OperationCanceledException; ToolErrorFilter also re-throws.
        var (kernel, exporter, _) = BuildPipeline(
            portImpl: (req, ct) => throw new OperationCanceledException("simulated cancellation"));

        // SK wraps OperationCanceledException in KernelFunctionCanceledException (a subclass),
        // so ThrowsAnyAsync<T> is used to accept derived types.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.InvokeAsync("ThingPlugin", "CreateThing"));

        // ToolTracingFilter's finally block disposes the execute_tool activity (exports it)
        // BEFORE the exception propagates — the event must be in the exported list.
        var events = exporter.ExportedActivities.SelectMany(a => a.Events).ToList();
        Assert.True(events.Any(e => e.Name == "inference.failed"),
            "Expected inference.failed event to be emitted before cancellation re-throw");
        var failed = events.Single(e => e.Name == "inference.failed");
        var tags = failed.Tags.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("cancelled", tags["affiant.error.kind"]?.ToString());
    }
}
