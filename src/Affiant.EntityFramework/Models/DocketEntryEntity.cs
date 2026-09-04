namespace Affiant.EntityFramework.Models;

/// <summary>
/// EF-tracked row for the <c>affiant.Docket</c> table — the durable review queue.
/// Mutable counterpart to the immutable <see cref="Affiant.Abstractions.Models.DocketEntry"/>
/// domain record. JSON columns store Affidavit and provenance data; conversion happens
/// in the store implementations.
/// </summary>
/// <remarks>
/// <para>
/// Every column added after the original set is a <em>later fact</em> the row accumulates: what a
/// reviewer chose, who agreed, what the executor reported, why the entry refuses every decision,
/// what a refused late decision carried, and what this entry supersedes. None of them overwrites
/// anything already on the row — that is the whole reason the accepted state lives in
/// <see cref="AmendedAffidavitJson"/> beside <see cref="AffidavitJson"/> rather than in it.
/// </para>
/// <para>
/// <see cref="CreatedAtTicks"/> and <see cref="ExpiresAtTicks"/> carry the same two instants as
/// sortable integers. They exist because SQLite has no native <c>DateTimeOffset</c>: its EF provider
/// stores one as ISO-8601 text and can translate neither an inequality nor an <c>ORDER BY</c> over it
/// into SQL, so before these columns a paged listing or a bounded sweep had to load every candidate
/// row and filter in memory — which is exactly what a bounded, cursor-paged store contract must not
/// do. Both stores query and order by the integer columns and return the
/// <c>DateTimeOffset</c> ones, so the two backends page identically.
/// </para>
/// </remarks>
public class DocketEntryEntity
{
    public Guid EntryId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string? ReviewerUserId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string AffidavitJson { get; set; } = "{}";
    public string ProvenanceChainsJson { get; set; } = "{}";
    public string? AmendmentsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = "Pending";
    public Guid? ResubmittedTo { get; set; }

    // ── The later facts (the conformance release) ────────────────────────────

    /// <summary>The tool that proposed the write. Null on rows filed before the column existed, where <see cref="OperationType"/> carries it.</summary>
    public string? ToolName { get; set; }

    /// <summary>
    /// The channel the proposal arrived on, as the host named it, or <c>null</c> when it named
    /// none (DK-1).
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// The requirement level the approval chain resolved, as the row was filed (DK-1). Nullable in
    /// the column for rows written before this release; read back as
    /// <c>ReviewerConfirmation</c>, which is what those rows were filed as.
    /// </summary>
    public string? Requirement { get; set; }

    /// <summary>What became of an approved write: <c>Unexecuted</c>, <c>Executed</c> or <c>Failed</c>. Null unless the row is approved.</summary>
    public string? Execution { get; set; }

    /// <summary>What the executor reported.</summary>
    public string? ExecutionDetail { get; set; }

    /// <summary>The decision record — <c>{ kind, reason, at }</c>.</summary>
    public string? DecisionJson { get; set; }

    /// <summary>The attestation — <c>{ by: { kind, … }, at, entryId }</c>.</summary>
    public string? AttestationJson { get; set; }

    /// <summary>The blocked marker — <c>{ code, … }</c>.</summary>
    public string? BlockedJson { get; set; }

    /// <summary>The composite approval this entry is one constituent of.</summary>
    public string? CompositeRef { get; set; }

    /// <summary>The state a reviewer's accepted amendments produced. Written beside the proposal, never over it.</summary>
    public string? AmendedAffidavitJson { get; set; }

    /// <summary>The provenance chains of <see cref="AmendedAffidavitJson"/>, stored the same way the proposal's are.</summary>
    public string? AmendedProvenanceChainsJson { get; set; }

    /// <summary>The amendments a refused late decision carried — <c>{ amendments, at, by }</c>.</summary>
    public string? PreservedAmendmentsJson { get; set; }

    /// <summary>The entry this one resubmits. The successor half of the lineage is <see cref="ResubmittedTo"/>.</summary>
    public Guid? Supersedes { get; set; }

    /// <summary>When the row left pending.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The protocol tag this row's shapes conform to.</summary>
    public string ProtocolVersion { get; set; } = Affiant.Abstractions.AffiantProtocol.Version;

    /// <summary><see cref="CreatedAt"/> as UTC ticks — the sortable scalar every filing-order page reads. See this type's remarks.</summary>
    public long CreatedAtTicks { get; set; }

    /// <summary><see cref="ExpiresAt"/> as UTC ticks — the sortable scalar the deadline comparison reads. See this type's remarks.</summary>
    public long ExpiresAtTicks { get; set; }

    /// <summary>
    /// <see cref="DecidedAt"/> as UTC ticks, or null while the row is pending — the sortable scalar
    /// retention measures a terminal row's age from. See this type's remarks.
    /// </summary>
    public long? DecidedAtTicks { get; set; }
}
