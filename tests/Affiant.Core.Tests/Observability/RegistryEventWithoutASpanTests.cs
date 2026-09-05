namespace Affiant.Core.Tests.Observability;

using System.Diagnostics;
using Affiant.Core.Observability;
using Xunit;

/// <summary>
/// The collection every test that listens to the framework's own <see cref="ActivitySource"/>
/// belongs to.
/// </summary>
/// <remarks>
/// <see cref="Activity.Current"/> and the set of registered <see cref="ActivityListener"/>s are
/// process-global. Two collections listening at once see each other's activities, so a test that
/// counts what it emitted counts somebody else's too. xUnit runs collections in parallel and tests
/// within one collection in sequence, so naming the collection is what makes the count the test's
/// own.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AmbientActivityCollection
{
    public const string Name = "ambient-activity";
}

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
[Collection(AmbientActivityCollection.Name)]
public sealed class RegistryEventWithoutASpanTests : IDisposable
{
    private readonly string _marker = $"probe-{Guid.NewGuid():N}";
    private readonly List<ActivityEvent> _events = [];
    private readonly object _gate = new();
    private readonly ActivityListener _listener;
    private readonly Activity? _restore = Activity.Current;

    public RegistryEventWithoutASpanTests()
    {
        Activity.Current = null;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Affiant.Framework",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,

            // Only this test's own events. Another collection emitting on the same source at the
            // same moment is somebody else's run, and counting it would make the assertion a race.
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _events.AddRange(activity.Events.Where(Mine));
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public void ADecisionRefusal_IsEmitted_WithNoAmbientActivity()
    {
        Assert.Null(Activity.Current);

        AffiantTelemetry.RecordDecisionUnauthorized(
            Guid.NewGuid(), _marker, "not-authorized", "decide", "member");

        Assert.Single(Recorded(), e => e.Name == "decision.unauthorized");
    }

    [Fact]
    public void AnEventEmittedInsideASpan_StaysOnThatSpan()
    {
        using (AffiantTelemetry.AffiantActivitySource.StartActivity("execute_tool"))
        {
            AffiantTelemetry.RecordDecisionUnauthorized(
                Guid.NewGuid(), _marker, "not-authorized", "decide", "member");
        }

        Assert.Single(Recorded(), e => e.Name == "decision.unauthorized");
    }

    /// <summary>This test's own events, identified by the conversation id it emitted them under.</summary>
    private bool Mine(ActivityEvent e) =>
        e.Tags.Any(t => t.Value as string == _marker);

    private ActivityEvent[] Recorded()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = _restore;
    }
}
