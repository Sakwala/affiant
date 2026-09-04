namespace Affiant.Core.Tests.Observability;

using System.Diagnostics;
using Affiant.Core.Observability;
using Xunit;

/// <summary>
/// TL-1: a registry event is observable whether or not the caller happens to be inside a span.
/// </summary>
/// <remarks>
/// The filing path runs inside <c>ToolTracingFilter</c>'s <c>execute_tool</c> activity, so events
/// emitted there were always visible. The decision and execution-report entry points are reached by
/// host code directly — a hub method, a queue worker, a relay — and everything they emitted was
/// discarded, including <c>decision.unauthorized</c>: the one event an operator most needs to be
/// able to count.
/// </remarks>
public sealed class RegistryEventWithoutASpanTests : IDisposable
{
    private readonly List<ActivityEvent> _events = [];
    private readonly ActivityListener _listener;
    private readonly Activity? _restore = Activity.Current;

    public RegistryEventWithoutASpanTests()
    {
        Activity.Current = null;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Affiant.Framework",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _events.AddRange(activity.Events),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public void ADecisionRefusal_IsEmitted_WithNoAmbientActivity()
    {
        Assert.Null(Activity.Current);

        AffiantTelemetry.RecordDecisionUnauthorized(
            Guid.NewGuid(), "conv-1", "not-authorized", "decide", "member");

        Assert.Contains(_events.ToArray(), e => e.Name == "decision.unauthorized");
    }

    [Fact]
    public void AnEventEmittedInsideASpan_StaysOnThatSpan()
    {
        using (AffiantTelemetry.AffiantActivitySource.StartActivity("execute_tool"))
        {
            AffiantTelemetry.RecordDecisionUnauthorized(
                Guid.NewGuid(), "conv-1", "not-authorized", "decide", "member");
        }

        Assert.Single(_events.ToArray(), e => e.Name == "decision.unauthorized");
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = _restore;
    }
}
