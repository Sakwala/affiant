namespace Affiant.Core.Extensions;

/// <summary>
/// Options for <see cref="ServiceCollectionExtensions.AddAffiantCore"/>.
/// Configures framework behavior at DI registration time.
/// </summary>
public sealed class AffiantCoreOptions
{
    /// <summary>
    /// Primary LLM provider name (e.g., "AzureOpenAI", "OpenAI").
    /// Passed to the SK kernel for automatic provider selection.
    /// Default: null (host must configure manually before use).
    /// </summary>
    public string? PrimaryProvider { get; set; }

    /// <summary>
    /// Fallback LLM provider name if the primary is unavailable.
    /// Per framework spec §5, DeterministicShortCircuit routes to fallback
    /// when primary fails. Default: null (no fallback).
    /// </summary>
    public string? FallbackProvider { get; set; }

    /// <summary>
    /// Default TTL for DocketEntry records before automatic expiry.
    /// Enforced by DocketExpiryService (registered separately in Affiant.Docket) and by
    /// <see cref="Affiant.Core.Services.ReviewGate"/>'s blocking-await window — both the
    /// <c>DocketEntry.ExpiresAt</c> stamp and the internal await timeout derive from this value.
    /// Default: TimeSpan.FromMinutes(30).
    /// </summary>
    public TimeSpan DefaultDocketTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far before a Pending DocketEntry's <c>ExpiresAt</c> the framework starts broadcasting
    /// <c>TransportEvent.DocketExpiring</c> warnings to the UI (Affiant.Docket's
    /// DocketExpiryService checks this on every tick). Re-emission across ticks inside the window
    /// is expected — clients must treat repeated warnings for the same docket as idempotent.
    /// Default: TimeSpan.FromMinutes(2).
    /// </summary>
    public TimeSpan DocketExpiryWarningWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether to initialize AffiantTelemetry (ActivitySource + Meter).
    /// If true, <see cref="Affiant.Core.Observability.AffiantTelemetry.AffiantActivitySource"/>
    /// and <see cref="Affiant.Core.Observability.AffiantTelemetry.AffiantMeter"/> are available
    /// for tracing and metrics collection. Default: true.
    /// </summary>
    public bool EnableObservability { get; set; } = true;

    /// <summary>
    /// Host-specific system prompt passed to the LLM on the first turn.
    /// Immutable after framework initialization (Normative Rule 1).
    /// Set by the host application via AddAffiantCore(). Default: null (no system prompt injected).
    /// </summary>
    public string? SystemPrompt { get; set; }
}
