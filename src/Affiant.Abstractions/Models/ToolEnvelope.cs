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
/// The write a tool proposes, as the HOST declares it: the shape, the entity it names, and the
/// fields it proposes, in the order the host declared them.
/// </summary>
/// <remarks>
/// <para>
/// It is what an entry id is derived from (GT-4), which is why it is stated rather than inferred: a
/// projection that reordered or renamed fields would otherwise change the identity of the row a
/// proposal files, and two implementations projecting the same call slightly differently would
/// disagree about which row it is. A caller that declares none leaves the gate to read the operation
/// off the record it proposes — which is what a resubmission does, having only the stored record to
/// read.
/// </para>
/// </remarks>
/// <param name="Kind">The protocol's two-valued shape vocabulary: <c>create</c> or <c>update</c>.</param>
/// <param name="EntityType">The kind of domain entity being written, named by the host.</param>
/// <param name="EntityId">The entity being written; null on a create (AF-3).</param>
/// <param name="Fields">The fields the operation proposes, in the order the host declared them.</param>
public sealed record ProposedOperation(
    string Kind,
    string EntityType,
    string? EntityId,
    IReadOnlyList<string> Fields)
{
    /// <summary>
    /// The operation an Affidavit describes — the reading used where no host declaration is at hand.
    /// </summary>
    /// <remarks>
    /// AF-1 makes an Affidavit's fields exactly the fields the operation proposes, so the two agree
    /// while that holds. A resubmission has nothing else to read: the row it replaces stores the
    /// record, not the call that produced it.
    /// </remarks>
    public static ProposedOperation From(Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        return new ProposedOperation(
            Operation.IsUpdateShaped(affidavit.OperationType) ? "update" : "create",
            affidavit.EntityType,
            affidavit.EntityId,
            [.. affidavit.Fields.Select(f => f.Name)]);
    }
}

/// <summary>
/// Write proposals — produces an envelope containing the proposed mutation,
/// never executes the write. The ReviewGate handles confirmation.
/// </summary>
/// <param name="ToolName">The tool the model called.</param>
/// <param name="Timestamp">When the call was made.</param>
/// <param name="Envelope">The proposed mutation, as an <c>Affidavit</c>.</param>
/// <param name="Arguments">
/// The arguments the model passed to the call, as the host received them, or <see langword="null"/>
/// when the proposal did not come from one (a capture prepared by a host, Sequence C).
///
/// <para>
/// They are not evidence — what is sworn about a field is what an interceptor or the inference port
/// says (PV-1) — and they are carried for one reason: an entry id is DERIVED from the tenant, the
/// conversation, the tool and the canonical form of the operation and its arguments (GT-4), so two
/// calls that differ only in their arguments are two proposals and a retry of the same call is a
/// replay of the same row. An implementation that left the arguments out of that material would
/// give two different writes the same identity.
/// </para>
/// </param>
/// <param name="Operation">
/// The write as the host declares it, or <see langword="null"/> to let the gate read it off the
/// record. Part of the material an entry id is derived from (GT-4).
/// </param>
public sealed record WriteProposal(
    string ToolName,
    DateTimeOffset Timestamp,
    object Envelope,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    ProposedOperation? Operation = null
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
