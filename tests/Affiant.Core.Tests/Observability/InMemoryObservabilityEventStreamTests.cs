namespace Affiant.Core.Tests.Observability;

using Affiant.Core.Observability;
using Xunit;

public class InMemoryObservabilityEventStreamTests
{
    // --- Test 1: publish without subscribers is a no-op ---

    [Fact]
    public void Publish_NoSubscribers_NoException()
    {
        var stream = new InMemoryObservabilityEventStream<int>();
        stream.Publish(42); // should not throw
    }

    // --- Test 2: single subscriber receives every publish ---

    [Fact]
    public void SingleSubscriber_ReceivesAllPublishes()
    {
        var stream = new InMemoryObservabilityEventStream<int>();
        var received = new List<int>();

        stream.Subscribe(e => received.Add(e));
        stream.Publish(1);
        stream.Publish(2);
        stream.Publish(3);

        Assert.Equal([1, 2, 3], received);
    }

    // --- Test 3: multiple subscribers each receive every publish ---

    [Fact]
    public void MultipleSubscribers_EachReceivesAllPublishes()
    {
        var stream = new InMemoryObservabilityEventStream<string>();
        var a = new List<string>();
        var b = new List<string>();

        stream.Subscribe(e => a.Add(e));
        stream.Subscribe(e => b.Add(e));
        stream.Publish("hello");
        stream.Publish("world");

        Assert.Equal(["hello", "world"], a);
        Assert.Equal(["hello", "world"], b);
    }

    // --- Test 4: Dispose unregisters cleanly ---

    [Fact]
    public void Dispose_UnregistersSubscriber()
    {
        var stream = new InMemoryObservabilityEventStream<int>();
        var received = new List<int>();

        var sub = stream.Subscribe(e => received.Add(e));
        stream.Publish(1);
        sub.Dispose();
        stream.Publish(2);

        Assert.Equal([1], received);
    }

    // --- Test 5: double-Dispose is a no-op ---

    [Fact]
    public void DoubleDispose_IsNoOp()
    {
        var stream = new InMemoryObservabilityEventStream<int>();
        var sub = stream.Subscribe(_ => { });
        sub.Dispose();
        sub.Dispose(); // should not throw
    }

    // --- Test 6: per-subscriber exception does not block other subscribers ---

    [Fact]
    public void SubscriberThrows_OtherSubscribersStillInvoked()
    {
        var stream = new InMemoryObservabilityEventStream<int>();
        var goodReceived = new List<int>();

        stream.Subscribe(_ => throw new InvalidOperationException("bad subscriber"));
        stream.Subscribe(e => goodReceived.Add(e));

        stream.Publish(99);

        Assert.Equal([99], goodReceived);
    }

    // --- Test 7: thread safety under concurrent Publish / Subscribe / Dispose ---

    [Fact]
    public async Task ThreadSafety_ConcurrentPublishSubscribeDispose_NoExceptions()
    {
        var stream = new InMemoryObservabilityEventStream<int>();
        var publishedCount = 0;
        var receivedCount = 0;

        // Pre-register a long-lived subscriber that counts events.
        stream.Subscribe(_ => Interlocked.Increment(ref receivedCount));

        const int iterations = 200;

        await Task.WhenAll(
            // Publisher task
            Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    stream.Publish(i);
                    Interlocked.Increment(ref publishedCount);
                }
            }),
            // Subscribe + dispose task
            Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    var sub = stream.Subscribe(_ => { });
                    sub.Dispose();
                }
            }),
            // Additional subscriber task
            Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    using var sub = stream.Subscribe(_ => { });
                    stream.Publish(i + 1000);
                }
            }));

        // No assertion on exact count since transient subscribers may or may not receive
        // each event — but no exception should have escaped.
        Assert.Equal(iterations, publishedCount);
        Assert.True(receivedCount >= iterations, $"Long-lived subscriber missed events: got {receivedCount}, expected >= {iterations}");
    }

    // --- Test 8: Subscribe null handler throws ---

    [Fact]
    public void Subscribe_NullHandler_Throws()
    {
        var stream = new InMemoryObservabilityEventStream<int>();
        Assert.Throws<ArgumentNullException>(() => stream.Subscribe(null!));
    }
}
