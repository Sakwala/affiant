namespace Affiant.TestInfrastructure.Telemetry;

using System.Collections;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

/// <summary>
/// Wires an OpenTelemetry in-memory trace exporter into a test host's service collection
/// so tests can assert on captured spans without emitting to a real backend.
/// </summary>
public sealed class InMemoryExporterHelper
{
    private readonly List<Activity> _inner = [];
    private readonly object _lock = new();
    private readonly SynchronizedActivityList _collection;

    public InMemoryExporterHelper()
    {
        _collection = new SynchronizedActivityList(_inner, _lock);
    }

    /// <summary>
    /// Snapshot of all activity spans collected since this helper was registered.
    /// Returns a copy — safe to enumerate while new activities arrive concurrently.
    /// </summary>
    public IReadOnlyList<Activity> ExportedActivities
    {
        get { lock (_lock) return [.. _inner]; }
    }

    /// <summary>
    /// Adds an in-memory activity exporter to <paramref name="services"/>.
    /// Call once from <c>WebApplicationFactory.ConfigureWebHost</c>.
    /// </summary>
    public void RegisterWithServices(IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(_collection));
    }

    public IEnumerable<Activity> GetCapturedActivities()
    {
        lock (_lock) return [.. _inner];
    }

    // Thread-safe ICollection<Activity> wrapper passed to AddInMemoryExporter.
    // OTel's InMemoryExporter calls Add() from its Export() method — the lock prevents
    // concurrent writes from racing with snapshot reads in ExportedActivities.
    private sealed class SynchronizedActivityList(List<Activity> inner, object lockObj) : ICollection<Activity>
    {
        public int Count { get { lock (lockObj) return inner.Count; } }
        public bool IsReadOnly => false;
        public void Add(Activity item) { lock (lockObj) inner.Add(item); }
        public void Clear() { lock (lockObj) inner.Clear(); }
        public bool Contains(Activity item) { lock (lockObj) return inner.Contains(item); }
        public void CopyTo(Activity[] array, int arrayIndex) { lock (lockObj) inner.CopyTo(array, arrayIndex); }
        public bool Remove(Activity item) { lock (lockObj) return inner.Remove(item); }
        public IEnumerator<Activity> GetEnumerator() { lock (lockObj) return inner.ToList().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
