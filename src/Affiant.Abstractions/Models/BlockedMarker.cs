using System.Text.Json.Serialization;

namespace Affiant.Abstractions.Models;

/// <summary>
/// Why no decision on an entry will be accepted, even though the entry sits in <c>pending</c>.
///
/// <para>
/// <b>AZ-4, CV-4</b> — <i>an implementation that receives a requirement level it does not run
/// records that level verbatim, files the entry pending with this marker, refuses every decision on
/// it, never executes it, and never degrades it to a weaker requirement.</i> A joint requirement
/// quietly satisfied by one approval is the failure this exists to prevent. A blocked entry's card
/// says so on its face and never claims a confirmation is being awaited — which is why the marker
/// travels on the Evidence Card envelope as a structure a reviewer surface can render, rather than
/// as a sentence in <see cref="Transport.EvidenceCardRequest.Warnings"/> that a client would have to
/// parse.
/// </para>
///
/// <para>
/// The marker's home is the Docket row — the entry is what is blocked, and the card only reports it.
/// <see cref="DocketEntry.Blocked"/> holds a value of this type, the guarded store transitions
/// refuse every decision on a row that carries one, and a card built from a blocked row carries the
/// row's own value. The same shape is what the wire carries, validated against
/// <c>schemas/0.1.0/blocked.schema.json</c>.
/// </para>
///
/// <para>
/// On the wire: <c>{ "code": …, … }</c>, and each code carries exactly the context that code makes
/// meaningful — a coverage refusal has no requirement level to report, so it has no <c>level</c>
/// property to leave null.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "code")]
[JsonDerivedType(typeof(RequirementNotImplemented), BlockedCode.RequirementNotImplemented)]
[JsonDerivedType(typeof(CoverageRefused), BlockedCode.CoverageRefused)]
public abstract record BlockedMarker
{
    /// <summary>
    /// The code this marker travels under, as it appears on the wire. Reading it never requires a
    /// type test.
    /// </summary>
    [JsonIgnore]
    public abstract string Code { get; }

    /// <summary>
    /// A requirement level this version recognises but does not run reached the pipeline. At v0.1
    /// those are <see cref="ReviewRequirement.ReferralRequired"/> and
    /// <see cref="ReviewRequirement.MultiParty"/>, whose semantics are reserved for protocol v0.2.
    /// </summary>
    /// <param name="Level">The requirement level that is not implemented, recorded verbatim.</param>
    public sealed record RequirementNotImplemented(ReviewRequirement Level) : BlockedMarker
    {
        /// <inheritdoc />
        [JsonIgnore]
        public override string Code => BlockedCode.RequirementNotImplemented;
    }

    /// <summary>
    /// A proposal came from a write-capable tool the host declared the gate cannot intercept (CV-4).
    /// Wire-up refuses such a tool outright unless the host declares it uncovered, in which case its
    /// proposals are still recorded on the Docket — blocked, never silently allowed to write.
    /// </summary>
    /// <param name="Category">The category the gate cannot cover.</param>
    /// <param name="ToolName">
    /// The tool the uncovered proposal came from, kept on the record so coverage can be re-assessed
    /// on a resubmission.
    /// </param>
    public sealed record CoverageRefused(CoverageCategory Category, string ToolName) : BlockedMarker
    {
        /// <inheritdoc />
        [JsonIgnore]
        public override string Code => BlockedCode.CoverageRefused;
    }
}

/// <summary>
/// The two blocked codes, as string constants so a producer and a consumer reference the same
/// literals. Nothing else is a blocked code.
/// </summary>
public static class BlockedCode
{
    /// <summary>Discriminator for <see cref="BlockedMarker.RequirementNotImplemented"/>.</summary>
    public const string RequirementNotImplemented = "requirement-not-implemented";

    /// <summary>Discriminator for <see cref="BlockedMarker.CoverageRefused"/>.</summary>
    public const string CoverageRefused = "coverage-refused";
}

/// <summary>
/// The categories of write-capable tool a gate cannot stand in front of (CV-4). Serialized in
/// kebab-case, the spelling <c>schemas/0.1.0/blocked.schema.json</c> freezes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoverageCategory>))]
public enum CoverageCategory
{
    /// <summary>A write-capable tool with no execute step for the gate to replace.</summary>
    [JsonStringEnumMemberName("no-execute")]
    NoExecute,

    /// <summary>A tool the model provider executes on its own side.</summary>
    [JsonStringEnumMemberName("provider-executed")]
    ProviderExecuted,

    /// <summary>A hosted MCP server-side write.</summary>
    [JsonStringEnumMemberName("hosted-mcp")]
    HostedMcp,
}
