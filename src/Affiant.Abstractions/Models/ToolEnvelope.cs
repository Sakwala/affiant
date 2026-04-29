using System.Text.Json.Serialization;

namespace Affiant.Abstractions.Models;

/// <summary>
/// Universal exchange type for all plugin returns. Every <c>[KernelFunction]</c>
/// method returns one of three variants serialized via <see cref="ToolEnvelopeExtensions.ToJsonString"/>.
///
/// The <c>$type</c> discriminator enables polymorphic JSON round-tripping, matching
/// Semantic Kernel's own <c>KernelContent</c> pattern.
///
/// Matches framework specification §2.4.
/// </summary>
[JsonDerivedType(typeof(ReadResult), "read")]
[JsonDerivedType(typeof(WriteProposal), "write")]
[JsonDerivedType(typeof(ToolError), "error")]
public abstract record ToolEnvelope(string ToolName, DateTimeOffset Timestamp);

/// <summary>
/// Read operations — returns markdown for dual-audience consumption (LLM + UI)
/// plus structured entity references for context extraction filters.
/// </summary>
public sealed record ReadResult(
    string ToolName,
    DateTimeOffset Timestamp,
    string Summary,
    string Markdown,
    EntityRef[] Entities
) : ToolEnvelope(ToolName, Timestamp);

/// <summary>
/// Write proposals — produces an envelope containing the proposed mutation,
/// never executes the write. The ReviewGate handles confirmation.
/// </summary>
public sealed record WriteProposal(
    string ToolName,
    DateTimeOffset Timestamp,
    object Envelope
) : ToolEnvelope(ToolName, Timestamp);

/// <summary>
/// Structured errors — plugins must never throw exceptions that propagate
/// to the LLM. Catch all exceptions and return a <c>ToolError</c> envelope.
/// </summary>
public sealed record ToolError(
    string ToolName,
    DateTimeOffset Timestamp,
    string Code,
    string Message,
    bool Retryable
) : ToolEnvelope(ToolName, Timestamp);
