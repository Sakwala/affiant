namespace Affiant.Core.Observability;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Affiant.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reports how deep the review queue is: <c>affiant.docket.pending</c>, an observable gauge of the
/// number of <c>Pending</c> Docket entries, one series per tenant.
///
/// <para>
/// <b>Why it exists (repo issue #66).</b> Every other instrument on
/// <see cref="AffiantTelemetry.AffiantMeter"/> measures an event — a turn's duration, a review's
/// outcome, a provider degrading. None of them answers the one question an operator needs before a
/// backlog becomes a database-load incident: <em>how many reviews are waiting right now?</em>
/// Without this gauge the first symptom of an unbounded review queue is store latency, because
/// there is nothing on a dashboard to alert on.
/// </para>
///
/// <para>
/// <b>What it costs, honestly.</b> A depth gauge has to read the store; there is no free version.
/// This one keeps the cost bounded and off the collection path:
/// </para>
/// <list type="bullet">
/// <item><b>Never blocks the collector.</b> The gauge callback returns the last sample and, if that
/// sample is older than <see cref="MinimumSampleInterval"/>, starts one background refresh. A
/// metrics scrape therefore costs a dictionary read, never a store round trip, and a scrape storm
/// cannot multiply into store load — at most one refresh is in flight at a time.</item>
/// <item><b>At most one listing per interval.</b> The refresh calls
/// <see cref="IDocketStore.ListAllPendingAsync"/> once, which is the same listing
/// <c>DocketExpiryService</c>'s sweep already performs every 30 seconds. At the 15-second default
/// this roughly doubles that one query's frequency and adds nothing else.</item>
/// <item><b>Bounded cardinality.</b> At most <see cref="MaxTenantSeries"/> tenant series are
/// reported; the remaining tenants are summed into a single <c>__other__</c> series, so a host with
/// ten thousand tenants cannot turn one gauge into ten thousand time series in the collector.</item>
/// <item><b>Stale by design.</b> The value can be up to <see cref="MinimumSampleInterval"/> plus one
/// refresh old, and the very first scrape after startup reports nothing at all if the first refresh
/// has not landed. A depth gauge is a trend signal; anyone treating it as a transactional count of
/// the store is reading it wrong.</item>
/// </list>
///
/// <para>
/// A host with no <see cref="IDocketStore"/> registered — the acknowledged read-only wiring
/// <c>AffiantWireUpValidator</c> permits — reports no measurements and logs once. It is not an error.
/// </para>
///
/// <para>
/// Registered as a singleton <see cref="IHostedService"/> by <c>AddAffiantCore</c> when
/// <c>AffiantCoreOptions.EnableObservability</c> is set, so exactly one instrument exists per
/// process. The gauge is created in the constructor and lives as long as the process does:
/// instruments are only released when their <see cref="Meter"/> is disposed, and
/// <see cref="AffiantTelemetry.AffiantMeter"/> is deliberately never disposed.
/// </para>
/// </summary>
public sealed class DocketDepthInstrument : IHostedService, IDisposable
{
    /// <summary>The gauge's instrument name.</summary>
    public const string InstrumentName = "affiant.docket.pending";

    /// <summary>The tag carrying the tenant a series counts entries for.</summary>
    public const string TenantTag = "tenant.id";

    /// <summary>
    /// The series every tenant past <see cref="MaxTenantSeries"/> is summed into, so cardinality is
    /// bounded by the framework rather than by how many tenants a host has.
    /// </summary>
    public const string OverflowTenantId = "__other__";

    /// <summary>
    /// How stale a sample may be before an observation starts a refresh. Chosen against
    /// <c>DocketExpiryService</c>'s 30-second sweep: one extra listing per sweep interval is a cost
    /// an operator can reason about.
    /// </summary>
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromSeconds(15);

    /// <summary>The most tenant series the gauge will report before folding the rest into one.</summary>
    public const int MaxTenantSeries = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocketDepthInstrument> _logger;
    private readonly CancellationTokenSource _stopping = new();

    private Measurement<long>[] _snapshot = [];
    private long _lastSampleTimestamp;
    private int _refreshInFlight;
    private int _storeUnavailableLogged;

    /// <summary>
    /// Creates the gauge. Constructing this type is what publishes <c>affiant.docket.pending</c> on
    /// <see cref="AffiantTelemetry.AffiantMeter"/>.
    /// </summary>
    public DocketDepthInstrument(
        IServiceScopeFactory scopeFactory,
        ILogger<DocketDepthInstrument> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // A timestamp far enough in the past that the first observation is stale and schedules a
        // refresh, without the negative-elapsed arithmetic a sentinel like long.MinValue invites.
        _lastSampleTimestamp = Stopwatch.GetTimestamp() - (long)(MinimumSampleInterval.TotalSeconds * Stopwatch.Frequency * 2);

        Gauge = AffiantTelemetry.AffiantMeter.CreateObservableGauge(
            InstrumentName,
            Observe,
            unit: "{entry}",
            description:
                "Docket entries currently awaiting review, by tenant. Sampled from the docket store " +
                "at most once every " + MinimumSampleInterval.TotalSeconds + "s and reported from a " +
                "cached snapshot, so a scrape never reads the store.");
    }

    /// <summary>The instrument this instance published. Test seam: identifies this instance's gauge.</summary>
    internal ObservableGauge<long> Gauge { get; }

    /// <summary>Takes the first sample so the gauge has a value before the first scrape.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ScheduleRefresh();
        return Task.CompletedTask;
    }

    /// <summary>Stops sampling. An in-flight refresh is cancelled, never awaited.</summary>
    /// <remarks>
    /// A host may dispose its services before it stops them — the ASP.NET Core test host does — so
    /// a second stop, or one after <see cref="Dispose"/>, is a no-op rather than a throw. A
    /// telemetry instrument must never be the reason a shutdown fails.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed: sampling is stopped by definition.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private IEnumerable<Measurement<long>> Observe()
    {
        var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSampleTimestamp));
        if (elapsed >= MinimumSampleInterval) ScheduleRefresh();
        return Volatile.Read(ref _snapshot);
    }

    private void ScheduleRefresh()
    {
        if (_stopping.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0) return;

        _ = Task.Run(RefreshAsync, CancellationToken.None);
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetService<IDocketStore>();
            if (store is null)
            {
                if (Interlocked.Exchange(ref _storeUnavailableLogged, 1) == 0)
                {
                    _logger.LogInformation(
                        "Affiant: no IDocketStore is registered, so the {Instrument} gauge reports " +
                        "no measurements. This is expected in a host that runs Affiant's " +
                        "read/inference half only.",
                        InstrumentName);
                }

                Volatile.Write(ref _snapshot, []);
                return;
            }

            var pending = await store.ListAllPendingAsync(_stopping.Token).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, Summarise(pending));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutting down. The last snapshot stands; nothing is scraping any more.
        }
        catch (Exception ex)
        {
            // A depth gauge must never take a host down, and must never mask a store outage with a
            // stale-looking zero: the previous snapshot stands and the failure is logged.
            _logger.LogWarning(ex,
                "Affiant: sampling the {Instrument} gauge failed; the gauge keeps reporting its " +
                "previous sample until the next refresh succeeds.",
                InstrumentName);
        }
        finally
        {
            Volatile.Write(ref _lastSampleTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _refreshInFlight, 0);
        }
    }

    private static Measurement<long>[] Summarise(IReadOnlyList<Abstractions.Models.DocketEntry> pending)
    {
        if (pending.Count == 0) return [];

        var byTenant = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in pending)
        {
            var tenant = string.IsNullOrEmpty(entry.TenantId) ? "unknown" : entry.TenantId;
            byTenant[tenant] = byTenant.GetValueOrDefault(tenant) + 1;
        }

        if (byTenant.Count <= MaxTenantSeries)
        {
            return [.. byTenant.Select(kv => new Measurement<long>(kv.Value, new KeyValuePair<string, object?>(TenantTag, kv.Key)))];
        }

        // Deepest queues first: the tenants an operator would act on keep their own series, and the
        // long tail becomes one. Ties break on the tenant id so the reported set is stable between
        // samples rather than shuffling with dictionary order.
        var ranked = byTenant
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToArray();

        var measurements = new List<Measurement<long>>(MaxTenantSeries + 1);
        for (var i = 0; i < MaxTenantSeries; i++)
        {
            measurements.Add(new Measurement<long>(
                ranked[i].Value, new KeyValuePair<string, object?>(TenantTag, ranked[i].Key)));
        }

        long overflow = 0;
        for (var i = MaxTenantSeries; i < ranked.Length; i++) overflow += ranked[i].Value;
        measurements.Add(new Measurement<long>(
            overflow, new KeyValuePair<string, object?>(TenantTag, OverflowTenantId)));

        return [.. measurements];
    }
}
