namespace Affiant.Core.Services;

using System.Collections.Concurrent;

/// <summary>
/// Hands out one <see cref="SemaphoreSlim"/>-backed serialization scope per session id, so a caller
/// holding a <see cref="SessionLockScope"/> for a given session strictly excludes every other caller
/// requesting that same session id until the scope is disposed — while a caller for a distinct
/// session id proceeds concurrently, unaffected.
///
/// <para>
/// <b>Intended use.</b> Neither <c>IChatSessionStore</c> nor <c>IDocketStore</c> serializes concurrent
/// callers for the same session id on its own — <c>IChatSessionStore.AppendMessagesAsync</c>'s own
/// remarks document that two overlapping calls can race on the max-ordinal read, and a turn that
/// loads persisted <c>ConversationContext</c> via <c>IDocketStore.LoadContextAsync</c>, mutates it,
/// and writes it back via <c>SaveContextAsync</c> has the same read-modify-write gap. Acquire the
/// scope for the full span of such a read-modify-write (or a first-turn session-row creation
/// check-then-insert), not just around the final write — the race lives in the gap between the read
/// and the write, not in the write call itself.
/// </para>
///
/// <para>
/// <b>Framework wiring status (as of this primitive's introduction, Area-5 P3).</b> Neither
/// Semantic Kernel adapter package nor the Agent Framework adapter package currently owns a call
/// site to wire this registry into — both were audited for one and found clean of any
/// <c>IDocketStore.SaveContextAsync</c>/<c>LoadContextAsync</c> or <c>IChatSessionStore.CreateAsync</c>/
/// <c>AppendMessagesAsync</c>/<c>SaveMessagesAsync</c> call. The Semantic Kernel adapter's session
/// rehydration service only reads (never writes back); the Agent Framework adapter touches neither
/// store interface at all; and the tool-invocation bridges in both adapters mutate only the
/// in-process, already-lock-guarded <see cref="ContextFabric"/> — never a store directly. The actual
/// load-mutate-save turn loop this primitive is built to guard (and the first-turn session-row
/// check-then-insert) is host-orchestrated end to end, in each host's own turn-loop entry point,
/// outside this repository. This primitive and its DI registration therefore ship unwired, for the
/// host wave to adopt directly at those call sites — retiring the single-process registry this type
/// promotes from (see the work item's cited reference implementation), and closing the gap recorded
/// for the host with no mitigation at all on its production store.
/// </para>
///
/// <para>
/// <b>Single-process only.</b> This registry is an in-memory <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// held by one <see cref="SessionLockRegistry"/> instance — it provides <i>no</i> serialization across
/// multiple host process instances (e.g. a scaled-out deployment behind a load balancer, where two
/// requests for the same session id can land on different instances). Closing that gap needs either
/// sticky-session routing keyed on the session id, or a distributed lock (e.g. a database-backed
/// advisory lock) — both out of scope for this primitive. A host running more than one instance of
/// itself must not treat this registry as a substitute for either.
/// </para>
///
/// <para>
/// <b>Unbounded growth.</b> Entries are never evicted — every distinct session id a process has ever
/// acquired a scope for keeps its <see cref="SemaphoreSlim"/> alive for that process's lifetime. This
/// is a deliberate omission, not an oversight: eviction needs a policy (idle-timeout? explicit
/// session-close hook? LRU with what bound?) that only a host's own session lifecycle can answer, and
/// guessing one here would be exactly the speculative abstraction this framework avoids. A host whose
/// distinct-session-id space grows without bound for the life of the process is responsible for its
/// own eviction — e.g. removing the entry itself once it owns a suitable hook, or restarting
/// periodically. For a bounded or slowly-growing session population this footprint never becomes a
/// practical concern.
/// </para>
/// </summary>
public sealed class SessionLockRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Waits for exclusive access to <paramref name="sessionId"/>'s serialization scope, then returns
    /// a handle that releases it back on <see cref="SessionLockScope.Dispose"/>. Callers for distinct
    /// session ids never block one another; callers for the same session id are strictly serialized in
    /// acquisition order.
    /// </summary>
    /// <param name="sessionId">The session id to serialize callers on. Never null or empty.</param>
    /// <param name="cancellationToken">
    /// Cancelling before the scope is acquired throws <see cref="OperationCanceledException"/> and
    /// never acquires the underlying semaphore — nothing to release.
    /// </param>
    public async Task<SessionLockScope> AcquireAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var semaphore = _locks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionLockScope(semaphore);
    }
}

/// <summary>
/// A held <see cref="SessionLockRegistry"/> scope for one session id. Releases the underlying
/// per-session lock exactly once, on the first <see cref="Dispose"/> call — safe to dispose more than
/// once, and intended to be held in a <see langword="using"/> declaration spanning the whole
/// read-modify-write it guards.
/// </summary>
public sealed class SessionLockScope : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _released;

    internal SessionLockScope(SemaphoreSlim semaphore) => _semaphore = semaphore;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _semaphore.Release();
    }
}
