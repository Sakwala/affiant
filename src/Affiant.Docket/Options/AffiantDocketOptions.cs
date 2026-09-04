using Affiant.Abstractions.Interfaces;

namespace Affiant.Docket.Options;

/// <summary>
/// Runtime options for the Docket's backend-neutral half — the ones
/// <see cref="Affiant.Docket.Services.DocketExpiryService"/> reads on every tick. Registered as a
/// singleton by <see cref="Affiant.Docket.Extensions.ServiceCollectionExtensions.AddAffiantDocket"/>
/// from the values a host sets on <see cref="DocketOptions"/>.
/// </summary>
/// <remarks>
/// Distinct from <see cref="DocketOptions"/> on purpose: that type is the registration-time builder
/// a host configures (and it carries <see cref="DocketOptions.UseInMemory"/>, which is a
/// registration act, not a setting); this type is the value the sweep resolves from DI at runtime.
/// A host that wants to replace the whole thing may register its own instance before calling
/// <c>AddAffiantDocket</c> — the registration is a <c>TryAdd</c>.
/// </remarks>
public sealed class AffiantDocketOptions
{
    /// <summary>
    /// What the scheduled sweep is allowed to reach. Defaults to
    /// <see cref="DocketScope.EntireStore"/> — every tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep is the host's own scheduled maintenance, not a caller acting on somebody's behalf,
    /// so the whole store is the right default: a deployment that ran a per-tenant sweep by default
    /// would silently stop expiring the tenants nobody remembered to configure.
    /// </para>
    /// <para>
    /// A host that partitions its Docket — one process per tenant, one worker per region — narrows
    /// this so each process expires only what it owns and two processes never contend for the same
    /// rows.
    /// </para>
    /// </remarks>
    public DocketScope SweepScope { get; set; } = DocketScope.EntireStore;

    /// <summary>The <see cref="ExpirySweepBatchSize"/> a host that sets nothing gets.</summary>
    public const int DefaultExpirySweepBatchSize = 100;

    private int _expirySweepBatchSize = DefaultExpirySweepBatchSize;

    /// <summary>
    /// The maximum number of due entries one <see cref="Affiant.Docket.Services.DocketExpiryService"/>
    /// tick transitions to <c>Expired</c>. Default: <see cref="DefaultExpirySweepBatchSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tick that finds more due entries than this leaves the remainder for the next tick, oldest
    /// deadline first (<see cref="Affiant.Abstractions.Interfaces.IDocketStore.ListExpiredAsync"/>
    /// orders by deadline for exactly that reason), so a backlog drains steadily instead of loading
    /// the whole Docket into one tick's memory. Expiry is a queryable state either way: a caller
    /// reading an entry whose deadline has passed sees <c>Expired</c> before the sweep reaches it.
    /// </para>
    /// <para>
    /// Raising this shortens the drain but lengthens the tick; lowering it does the reverse. Set it
    /// above the number of entries a deployment can plausibly have fall due inside one 30-second
    /// tick and the sweep never falls behind.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int ExpirySweepBatchSize
    {
        get => _expirySweepBatchSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _expirySweepBatchSize = value;
        }
    }

    /// <summary>The <see cref="ExpirySweepBatchesPerTick"/> a host that sets nothing gets.</summary>
    public const int DefaultExpirySweepBatchesPerTick = 10;

    private int _expirySweepBatchesPerTick = DefaultExpirySweepBatchesPerTick;

    /// <summary>
    /// The maximum number of <see cref="Affiant.Abstractions.Interfaces.IDocketStore.ExpireDueAsync"/>
    /// calls one <see cref="Affiant.Docket.Services.DocketExpiryService"/> tick makes before yielding
    /// to the next tick. Default: <see cref="DefaultExpirySweepBatchesPerTick"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store reports whether more due entries remain, so a tick could keep calling until the
    /// queue is drained. This cap is what stops it: a deployment that has just come back from a long
    /// outage has a backlog measured in hours of expiries, and a tick that drained it in one pass
    /// would hold a database connection and a service scope for the duration — the unbounded sweep in
    /// bounded clothing.
    /// </para>
    /// <para>
    /// The product of this and <see cref="ExpirySweepBatchSize"/> is what one tick can expire; a
    /// larger backlog drains over the ticks that follow. Nothing is mis-reported in the meantime: an
    /// entry past its deadline reads expired before the sweep reaches it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int ExpirySweepBatchesPerTick
    {
        get => _expirySweepBatchesPerTick;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _expirySweepBatchesPerTick = value;
        }
    }
}
