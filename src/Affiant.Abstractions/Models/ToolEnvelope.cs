using System.Text.Json.Serialization;

namespace Affiant.Abstractions.Models;

/// <summary>
/// Universal exchange type for all plugin returns. Every <c>[KernelFunction]</c>
/// method returns one of three variants serialized via <see cref="ToolEnvelopeExtensions.ToJsonString"/>.
///
/// <para>
/// <b>AF-5</b> — <i>a tool's result on the wire is one discriminated union of three kinds, carried
/// on a single discriminator property. A consumer switches on the discriminator, never on the
/// presence of fields.</i> A gated write tool's result is always the proposal kind (GT-6): a model
/// reading it learns that the write is pending or that a Standing Order approved it, never that it
/// happened, because the gate does not execute. A refusal the gate raises is the error kind
/// carrying its code.
/// </para>
///
/// <para>
/// <b>The discriminator is <c>kind</c> from v0.1 (SR-3).</b> It was <c>$type</c> — inherited from
/// Semantic Kernel's <c>KernelContent</c> pattern rather than chosen — through
/// <c>1.0.0-beta.1</c>. <c>$</c>-prefixed names are reserved by JSON Schema, which is why the v0.1
/// schemas cannot spell the old one, and a discriminator a schema cannot name is a discriminator
/// nothing can validate. This is a breaking wire change: a client that switches on <c>$type</c>
/// reads no discriminator at all after the upgrade and falls through to its default arm. See the
/// CHANGELOG's upgrade note.
/// </para>
///
/// Matches framework specification §2.4.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
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
