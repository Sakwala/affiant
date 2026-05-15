namespace Affiant.Abstractions.Interfaces;

public interface IObservabilityEventStream<T> where T : notnull
{
    void Publish(T @event);
    IDisposable Subscribe(Action<T> handler);
}
