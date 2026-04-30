using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Affiant.Core.Observability;

/// <summary>
/// Process-global telemetry surface for the Affiant framework.
/// Exposes the canonical ActivitySource, Meter, and pre-defined instruments
/// that all observability components write against.
/// </summary>
public static class AffiantTelemetry
{
    public static readonly ActivitySource AffiantActivitySource = new("Affiant.Framework");

    public static readonly Meter AffiantMeter = new("Affiant.Framework");

    // Histograms — duration measurements in milliseconds
    public static readonly Histogram<double> TurnDuration =
        AffiantMeter.CreateHistogram<double>("affiant.turn.duration", unit: "ms");

    public static readonly Histogram<double> ReviewWaitDuration =
        AffiantMeter.CreateHistogram<double>("affiant.review.wait_duration", unit: "ms");

    // Counters — tagged event counts
    // Tags: purpose ∈ { "orchestration", "inference" }
    public static readonly Counter<long> TokenUsage =
        AffiantMeter.CreateCounter<long>("affiant.token.usage");

    // Tags: result ∈ { "approved", "rejected", "expired", "standing_order" }
    public static readonly Counter<long> ReviewOutcome =
        AffiantMeter.CreateCounter<long>("affiant.review.outcome");

    // Tags: reason ∈ { "primary_failure", "both_failed" }
    public static readonly Counter<long> ProviderDegraded =
        AffiantMeter.CreateCounter<long>("affiant.provider.degraded");
}
