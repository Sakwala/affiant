namespace Affiant.Core.Observability;

using Affiant.Abstractions.Interfaces;

/// <summary>
/// Default in-process pub/sub backing <see cref="IObservabilityEventStream{T}"/>.
/// Thread-safe snapshot-based publish; per-subscriber exception swallowing.
/// Hosts override by registering their own implementation before <c>AddAffiantCore()</c>.
/// </summary>
public sealed class InMemoryObservabilityEventStream<T> : IObservabilityEventStream<T>
    where T : notnull
{
    private readonly object _gate = new();
    private readonly List<Action<T>> _subscribers = new();

    public void Publish(T @event)
    {
        Action<T>[] snapshot;
        lock (_gate) { snapshot = _subscribers.ToArray(); }
        foreach (var s in snapshot)
        {
            try { s(@event); } catch { /* swallow per-subscriber failure to keep publication best-effort */ }
        }
    }

    public IDisposable Subscribe(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) { _subscribers.Add(handler); }
        return new Subscription(this, handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly InMemoryObservabilityEventStream<T> _owner;
        private readonly Action<T> _handler;

        public Subscription(InMemoryObservabilityEventStream<T> owner, Action<T> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            lock (_owner._gate) { _owner._subscribers.Remove(_handler); }
        }
    }
}
