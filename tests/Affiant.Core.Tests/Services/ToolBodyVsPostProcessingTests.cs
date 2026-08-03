namespace Affiant.Core.Tests.Services;

using System.Diagnostics;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Area-3 P2 ruling 3 mutation locks: the "tool body" vs "post-processing" distinction that
/// governs <see cref="ToolErrorFilter"/>'s catch/retry decision via
/// <see cref="ToolInvocationContext.ToolExecuted"/>.
///
/// Drives the real <see cref="ToolInvocationPipeline"/> with the real <see cref="ToolErrorFilter"/>
/// and a real <see cref="ContextExtractor"/> subclass — no bridge/middleware needed, since the
/// contract lives entirely in the neutral layer (the whole point of ruling 3's design note: "design
/// it cleanly in the neutral layer so BOTH adapters inherit it").
/// </summary>
public class ToolBodyVsPostProcessingTests
{
    private static ActivityListener FrameworkListener() => new()
    {
        ShouldListenTo = source => source.Name == "Affiant.Framework",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    private static string GenuineReadResultJson(string toolName) =>
        new ReadResult(
            ToolName: toolName,
            Timestamp: DateTimeOffset.UtcNow,
            Summary: "ok",
            Markdown: "ok",
            Entities: [new EntityRef("Widget", "w-1", "Widget 1", new Dictionary<string, object> { ["x"] = 1 })]
        ).ToJsonString();

    private static ToolInvocationRequest Request(string functionName) =>
        new(functionName, "P", new Dictionary<string, object?>());

    private static ToolInvocationPipeline Pipeline(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>());

    // ── Counting-fake tool + throwing extractor ─────────────────────────────────

    [Fact]
    public async Task ThrowingExtractor_ToolRunsOnce_GenuineResultPreserved_ExtractorFailedEventObserved()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);
        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var toolCallCount = 0;
        var genuineResult = GenuineReadResultJson("SearchWidgets");

        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        services.AddSingleton<IToolInvocationFilter>(new ThrowingContextExtractor());
        var sp = services.BuildServiceProvider();

        var ctx = await Pipeline(sp).RunAsync(
            Request("SearchWidgets"),
            f => f,
            neutral =>
            {
                toolCallCount++;
                neutral.Result = genuineResult;
                neutral.ToolExecuted = true; // set by the real bridge the instant the tool succeeds
                return Task.CompletedTask;
            });

        Assert.Equal(1, toolCallCount); // tool executes exactly ONCE
        Assert.Equal(genuineResult, ctx.Result); // the model sees the genuine result, untouched

        var evt = Assert.Single(span!.Events, e => e.Name == "affiant.extractor.failed");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(nameof(ThrowingContextExtractor), tags["extractor.type"]);
        Assert.Equal("SearchWidgets", tags["tool.name"]);
        Assert.Equal("InvalidOperationException", tags["exception.type"]);
    }

    // ── Retryable tool failure: tool executes TWICE, extractor runs ONCE ────────

    [Fact]
    public async Task RetryableToolFailure_ToolRunsTwice_ExtractorRunsExactlyOnce_OnTheFinalResult()
    {
        var toolCallCount = 0;
        var extractCallCount = 0;
        var genuineResult = GenuineReadResultJson("SearchWidgets");

        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        services.AddSingleton<IToolInvocationFilter>(new CountingContextExtractor(() => extractCallCount++));
        var sp = services.BuildServiceProvider();

        var ctx = await Pipeline(sp).RunAsync(
            Request("SearchWidgets"),
            f => f,
            neutral =>
            {
                toolCallCount++;
                if (toolCallCount == 1)
                {
                    // ToolExecuted stays false — the real bridges only set it AFTER next() returns
                    // without throwing, exactly like this failing first attempt.
                    throw new TimeoutException("db timed out");
                }

                neutral.Result = genuineResult;
                neutral.ToolExecuted = true;
                return Task.CompletedTask;
            });

        Assert.Equal(2, toolCallCount); // one failed attempt + one retry
        Assert.Equal(1, extractCallCount); // extractor never ran on the failed attempt, ran once on the retry
        Assert.Equal(genuineResult, ctx.Result); // the retry's genuine result, untouched by extraction
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class ThrowingContextExtractor()
        : ContextExtractor(new ContextFabric(), NullLogger<ThrowingContextExtractor>.Instance)
    {
        protected override bool MatchesTool(string toolName) => true;

        protected override Task ExtractAsync(ReadResult result, ToolInvocationContext context) =>
            throw new InvalidOperationException("extractor bug");
    }

    private sealed class CountingContextExtractor(Action onExtract)
        : ContextExtractor(new ContextFabric(), NullLogger<CountingContextExtractor>.Instance)
    {
        protected override bool MatchesTool(string toolName) => true;

        protected override Task ExtractAsync(ReadResult result, ToolInvocationContext context)
        {
            onExtract();
            return Task.CompletedTask;
        }
    }
}
