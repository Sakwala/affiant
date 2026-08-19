namespace Affiant.Core.Extensions;

/// <summary>
/// Options for <see cref="ServiceCollectionExtensions.AddAffiantCore"/>.
/// Configures framework behavior at DI registration time.
/// </summary>
public sealed class AffiantCoreOptions
{
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

    /// <summary>
    /// Explicit host acknowledgment that this application runs without a complete review loop — i.e.
    /// no <c>IStreamingTransport</c> and/or no <c>IDocketStore</c> is registered anywhere, so
    /// <see cref="Affiant.Core.Services.ReviewGate"/> cannot file a write proposal for review.
    /// <see cref="Affiant.Core.Validation.AffiantWireUpValidator"/> throws at startup by default when
    /// either is missing (area-8 ruling 6, 2026-08-20); setting this to <c>true</c> downgrades that to
    /// one startup warning per missing contract, mirroring
    /// <c>AgentFrameworkOptions.AcknowledgeUncoveredTools</c>'s explicit, auditable, never-silent
    /// shape. Intended for a host that deliberately uses Affiant's read/inference half only.
    /// Default: <c>false</c> (incomplete review wiring is refused).
    /// </summary>
    public bool AcknowledgeMissingReviewWiring { get; set; }
}
