namespace Affiant.Core.Tests.Observability;

using System.Diagnostics;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Pins the <c>affiant.tool_error</c> ActivityEvent contract emitted by <see cref="ToolErrorFilter"/>
/// on the tool-error path. Downstream observability (dashboards, alerting on non-retryable tool
/// failures) keys off these exact tag names — <c>tool_error.code</c>, <c>tool_error.retryable</c>,
/// <c>exception.type</c> — so a rename or drop here is a breaking telemetry change that must fail a test.
/// </summary>
public class ToolErrorTelemetryTests
{
    private static ActivityListener FrameworkListener() => new()
    {
        ShouldListenTo = source => source.Name == AffiantTelemetry.AffiantActivitySource.Name,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    private static ToolInvocationContext Ctx() => new()
    {
        FunctionName = "DoWrite",
        PluginName = "WritePlugin",
        Arguments = new Dictionary<string, object?>(),
        Services = new ServiceCollection().BuildServiceProvider(),
    };

    [Fact]
    public async Task NonRetryableFailure_EmitsToolErrorEvent_WithCodeRetryableAndExceptionTypeTags()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);

        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var filter = new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance);
        await filter.OnToolInvocationAsync(Ctx(), _ => throw new InvalidOperationException("boom"));

        var evt = Assert.Single(span!.Events, e => e.Name == "affiant.tool_error");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("VALIDATION_FAILED", tags["tool_error.code"]);
        Assert.Equal(false, tags["tool_error.retryable"]);
        Assert.Equal("InvalidOperationException", tags["exception.type"]);
    }

    [Fact]
    public async Task RetryableFailure_EmitsToolErrorEvent_MarkedRetryable()
    {
        using var listener = FrameworkListener();
        ActivitySource.AddActivityListener(listener);

        using var span = AffiantTelemetry.AffiantActivitySource.StartActivity("invoke_agent");
        Assert.NotNull(span);

        var filter = new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance);
        await filter.OnToolInvocationAsync(Ctx(), _ => throw new TimeoutException("db timed out"));

        // First (retryable) attempt emits a retryable event; the retry then fails non-retryably.
        var firstEvent = span!.Events.First(e => e.Name == "affiant.tool_error");
        var tags = firstEvent.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("DB_TIMEOUT", tags["tool_error.code"]);
        Assert.Equal(true, tags["tool_error.retryable"]);
        Assert.Equal("TimeoutException", tags["exception.type"]);
    }
}
