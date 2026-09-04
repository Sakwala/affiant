namespace Affiant.Docket.Options;

/// <summary>
/// Store-selection builder for <see cref="Affiant.Docket.Extensions.ServiceCollectionExtensions.AddAffiantDocket"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The SQL branches moved out of this type (affiant#35, area-8 ruling 1, 2026-08-20.)</b>
/// <c>UsePostgres(connectionString)</c> and <c>UseSqlite(connectionString)</c> used to live here and
/// selected <c>PostgresDocketStore</c>/<c>SqliteDocketStore</c>. Those two stores take
/// <c>Affiant.EntityFramework</c>'s <c>AffiantDbContext</c> as a constructor dependency, which forced
/// <c>Affiant.Docket</c> to carry a <c>ProjectReference</c> onto <c>Affiant.EntityFramework</c> —
/// an adapter-to-adapter edge the repo's own layering invariant forbids (see <c>CLAUDE.md</c>,
/// "Layering invariant"), and a hard NuGet dependency that dragged EF Core + the SQLite and Npgsql
/// providers onto every consumer of this package, including one that only ever wanted
/// <see cref="Affiant.Docket.Stores.InMemoryDocketStore"/>.
/// </para>
/// <para>
/// Both SQL stores now live in <c>Affiant.EntityFramework</c> next to the
/// <c>DocketEntryEntity</c>/<c>DocketEntityConfiguration</c>/migrations that were always there, and
/// are registered by that package's <c>AddAffiantEntityFramework(ef =&gt; ef.UsePostgres(...))</c> /
/// <c>ef.UseSqlite(...)</c> alongside the <c>IChatSessionStore</c> it already registered the same way.
/// A SQL-backed host therefore selects its Docket backend through <c>AddAffiantEntityFramework</c>
/// and calls <c>AddAffiantDocket()</c> with no store selection at all — it still needs that call for
/// <see cref="Affiant.Docket.Services.DocketExpiryService"/>, which is backend-neutral and stays here.
/// </para>
/// <para>
/// This was a clean pre-1.0 break with no compatibility shim, per the repo's
/// "No backwards-compatibility shims pre-1.0" rule: there are no published consumers (first publish
/// is <c>1.0.0-beta.1</c>).
/// </para>
/// </remarks>
public sealed class DocketOptions
{
    internal bool UseInMemoryProvider { get; private set; }

    internal AffiantDocketOptions Runtime { get; } = new();

    /// <summary>
    /// The maximum number of due entries one <see cref="Affiant.Docket.Services.DocketExpiryService"/>
    /// tick transitions to <c>Expired</c>. Default:
    /// <see cref="AffiantDocketOptions.DefaultExpirySweepBatchSize"/>. See
    /// <see cref="AffiantDocketOptions.ExpirySweepBatchSize"/>, which this writes through to.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int ExpirySweepBatchSize
    {
        get => Runtime.ExpirySweepBatchSize;
        set => Runtime.ExpirySweepBatchSize = value;
    }

    /// <summary>
    /// Registers <see cref="Affiant.Docket.Stores.InMemoryDocketStore"/> as the process-local
    /// <c>IDocketStore</c> — the only backend this package still implements. Nothing is persisted
    /// across process restarts; use <c>AddAffiantEntityFramework</c>'s SQLite or PostgreSQL provider
    /// for a durable Docket (see this type's remarks).
    /// </summary>
    public void UseInMemory() => UseInMemoryProvider = true;
}
