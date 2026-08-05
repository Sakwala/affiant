namespace Affiant.Core.Tests.Services;

using Affiant.Core.Services;
using Xunit;

/// <summary>
/// Area-5 P3: the per-session turn-serialization primitive. Mirrors the properties HR Portal's
/// host-side <c>ConversationLockRegistry</c> (the reference implementation this promotes) relied on
/// informally — proven here directly against the framework type instead.
/// </summary>
public class SessionLockRegistryTests
{
    [Fact]
    public async Task AcquireAsync_SameSessionId_StrictlySerializesConcurrentCallers()
    {
        var registry = new SessionLockRegistry();
        var concurrentCount = 0;
        var maxObserved = 0;
        var gate = new object();

        async Task RunAsync()
        {
            using var scope = await registry.AcquireAsync("session-A");

            var current = Interlocked.Increment(ref concurrentCount);
            lock (gate)
                maxObserved = Math.Max(maxObserved, current);

            await Task.Delay(15);

            Interlocked.Decrement(ref concurrentCount);
        }

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RunAsync()));

        Assert.Equal(1, maxObserved);
    }

    [Fact]
    public async Task AcquireAsync_DistinctSessionIds_ProceedInParallel()
    {
        var registry = new SessionLockRegistry();
        var aHeld = new TaskCompletionSource();
        var bHeld = new TaskCompletionSource();

        // If the registry ever shared one lock across session ids, this deadlocks: each side must
        // observe the other holding its OWN lock before releasing its own — impossible unless the
        // two locks are genuinely independent.
        async Task<bool> RunAsync(string sessionId, TaskCompletionSource ownHeld, Task otherHeld)
        {
            using var scope = await registry.AcquireAsync(sessionId);
            ownHeld.SetResult();
            await otherHeld;
            return true;
        }

        var taskA = RunAsync("session-A", aHeld, bHeld.Task);
        var taskB = RunAsync("session-B", bHeld, aHeld.Task);

        await Task.WhenAny(Task.WhenAll(taskA, taskB), Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(taskA.IsCompletedSuccessfully && taskB.IsCompletedSuccessfully,
            "distinct session ids blocked on one another — the registry is not per-session");
    }

    [Fact]
    public async Task Dispose_ReleasesTheLock_AllowingTheNextAcquireToProceed()
    {
        var registry = new SessionLockRegistry();
        var scope1 = await registry.AcquireAsync("session-A");

        var acquireTask = registry.AcquireAsync("session-A");
        await Task.Delay(30);
        Assert.False(acquireTask.IsCompleted);

        scope1.Dispose();

        var scope2 = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        scope2.Dispose();
    }

    [Fact]
    public async Task Dispose_CalledTwice_ReleasesOnlyOnce()
    {
        var registry = new SessionLockRegistry();
        var scope = await registry.AcquireAsync("session-A");

        scope.Dispose();
        scope.Dispose();

        using var scope2 = await registry.AcquireAsync("session-A").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquireAsync_CancelledBeforeAcquired_ThrowsAndReleasesNothing()
    {
        var registry = new SessionLockRegistry();
        using var scope1 = await registry.AcquireAsync("session-A");

        using var cts = new CancellationTokenSource();
        var acquireTask = registry.AcquireAsync("session-A", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => acquireTask);

        // The cancelled waiter never took the semaphore, so it is still exactly session-A's original
        // holder's to release — a fresh acquire must still block until scope1 disposes.
        var stillHeld = registry.AcquireAsync("session-A");
        await Task.Delay(30);
        Assert.False(stillHeld.IsCompleted);

        scope1.Dispose();
        (await stillHeld).Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AcquireAsync_NullOrEmptySessionId_Throws(string? sessionId)
    {
        var registry = new SessionLockRegistry();

        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException (a subtype) for null and
        // ArgumentException itself for empty — ThrowsAnyAsync accepts either, ThrowsAsync would not.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => registry.AcquireAsync(sessionId!));
    }

    [Fact]
    public async Task AcquireAsync_ManyDistinctSessionIds_AllIndependentlyAcquirable()
    {
        // Growth is unbounded by design (see SessionLockRegistry's XML docs) — this is a sanity check
        // that accumulating many distinct sessions never blocks or throws, not a boundedness proof.
        var registry = new SessionLockRegistry();

        for (var i = 0; i < 500; i++)
        {
            using var scope = await registry.AcquireAsync($"session-{i}");
        }
    }
}
