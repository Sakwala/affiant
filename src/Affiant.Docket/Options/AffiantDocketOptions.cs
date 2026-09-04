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
}
