using System.Diagnostics;

namespace Affiant.Testing.ComplianceHarness.Conformance.Ports;

/// <summary>
/// The telemetry sink the driver owns (<c>DRIVER.md</c> §2): it records the keys the framework
/// emitted, so <c>expect.telemetry</c> and <c>expect.telemetryAbsent</c> can be answered.
/// </summary>
/// <remarks>
/// <para>
/// <b>1.0.0-beta.1 has no telemetry port.</b> Telemetry is <c>static readonly ActivitySource</c> and
/// <c>Meter</c> fields on <c>AffiantTelemetry</c> — process-global, not injectable — so the only way
/// to observe it is to listen. This listens to both of the framework's activity sources and records
/// every activity name and span-event name it sees.
/// </para>
/// <para>
/// <b>The two key sets are disjoint.</b> The rulebook's registry names
/// <c>affidavit.filed</c>, <c>docket.transition</c>, <c>standing-order.fired</c> and six more; the
/// framework emits <c>affidavit.projected</c>, <c>affiant.review.broadcast_failed</c>,
/// <c>inference.completed</c> and so on. Not one name is shared. Every <c>expect.telemetry</c>
/// clause therefore fails and every <c>expect.telemetryAbsent</c> clause holds — which is a true
/// statement about this release, and the reason the mapping is written down rather than papered
/// over by translating names the framework never emitted.
/// </para>
/// </remarks>
internal sealed class TelemetrySink : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public TelemetrySink()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Affiant", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => Record(activity.OperationName),
            ActivityStopped = activity =>
            {
                foreach (var e in activity.Events)
                {
                    Record(e.Name);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Every telemetry key the framework emitted during this fixture, in no particular order.</summary>
    public IReadOnlySet<string> Emitted
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_emitted, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Records a key the driver saw outside the activity pipeline — an event stream publication, say.</summary>
    public void Record(string key)
    {
        lock (_gate)
        {
            _emitted.Add(key);
        }
    }

    public void Dispose() => _listener.Dispose();
}
