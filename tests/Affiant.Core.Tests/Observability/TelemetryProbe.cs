namespace Affiant.Core.Tests.Observability;

using System.Diagnostics;
using Affiant.Core.Observability;

/// <summary>
/// One test's private window onto the framework's span events: registers its own
/// <see cref="ActivityListener"/>, starts a root activity on
/// <see cref="AffiantTelemetry.AffiantActivitySource"/>, and collects the events emitted while it
/// is alive.
///
/// <para>
/// <b>Isolation, deliberately (repo issue #17).</b> That issue is a 36-test flake caused by
/// <see cref="ActivitySource"/>/<see cref="ActivityListener"/> static state leaking between test
/// projects under some VSTest scheduling orders. Two properties here keep this suite out of it, and
/// both are load-bearing:
/// </para>
/// <list type="number">
/// <item><see cref="ActivityListener.ShouldListenTo"/> matches the framework source by
/// <em>reference</em>, not by name, so a listener a different assembly registered for a
/// same-named source is not this probe's, and this probe is not theirs.</item>
/// <item>The listener and the root activity are both released on <see cref="Dispose"/>, which
/// <see langword="using"/> guarantees even when an assertion throws. A leaked listener is what
/// turns one test's telemetry into another test's.</item>
/// </list>
///
/// <para>
/// Events are read from the root activity, which is per async flow, so two tests running in
/// parallel collect their own events and not each other's.
/// </para>
/// </summary>
internal sealed class TelemetryProbe : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly Activity? _root;

    public TelemetryProbe(string rootName = "test_root")
    {
        // Force AffiantTelemetry's static initialisation BEFORE the listener is registered, and hold
        // the source in a local so the ShouldListenTo callback cannot trigger that initialisation
        // itself. This ordering is load-bearing, and getting it wrong is a reproduction of issue #17:
        // AddActivityListener notifies the sources that exist when it runs, so a listener whose
        // callback is what first constructs Affiant's ActivitySource creates that source DURING the
        // notification pass and the source never learns about the listener. StartActivity then
        // returns null, no events are recorded, and the test fails with an empty event list and no
        // hint as to why. It only bites the first probe in a process, which is exactly why the flake
        // in #17 depends on test scheduling order.
        var source = AffiantTelemetry.AffiantActivitySource;

        _listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(_listener);
        _root = source.StartActivity(rootName);
    }

    /// <summary>Every event emitted onto this probe's root activity, in order.</summary>
    public IReadOnlyList<ActivityEvent> Events => _root?.Events.ToList() ?? [];

    /// <summary>The one event named <paramref name="name"/>. Throws when there is not exactly one.</summary>
    public ActivityEvent Single(string name) => Events.Single(e => e.Name == name);

    /// <summary>Whether any event named <paramref name="name"/> was emitted.</summary>
    public bool Saw(string name) => Events.Any(e => e.Name == name);

    /// <summary>The attributes on the one event named <paramref name="name"/>.</summary>
    public IReadOnlyDictionary<string, object?> Attributes(string name) =>
        Single(name).Tags.ToDictionary(t => t.Key, t => t.Value);

    public void Dispose()
    {
        _root?.Dispose();
        _listener.Dispose();
    }
}
