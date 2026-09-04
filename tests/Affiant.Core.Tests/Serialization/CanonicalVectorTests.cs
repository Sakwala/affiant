namespace Affiant.Core.Tests.Serialization;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Core.Serialization;
using Xunit;

/// <summary>
/// The seven normative canonical-serialization vectors (SR-1), one test each.
///
/// <para>
/// A vector is an input document, the exact UTF-8 bytes SR-1 produces for it, and the SHA-256 of
/// those bytes. They are the rulebook's own files, vendored under <c>tests/protocol/</c> from
/// <c>Sakwala/affiant-protocol</c> at commit <c>977e141</c> (<c>v0.1.0-2-g977e141</c>) — never
/// re-derived here, because a test that computed its own expectation would prove only that this
/// implementation agrees with itself.
/// </para>
///
/// <para>
/// <b>What each vector is for.</b> Two of the seven are not Affidavits at all: <c>key-order-stress</c>
/// is a document whose keys are written in reverse order at every level, including the cases a naive
/// comparator gets wrong (a private-use character must sort before an emoji, which a UTF-16 code-unit
/// comparison gets backwards); and <c>number-forms</c> is a table of every number form the rule has
/// to decide. Four are Affidavit-shaped and pin the record's own bytes. The seventh,
/// <c>wire-evidence-card-request-amended</c>, is the same Affidavit as the sixth with a reviewer's
/// accepted amendments applied — and its hash differs, which is the whole point: an execution grant
/// minted for the proposal a reviewer was shown must not validate the proposal they amended.
/// </para>
/// </summary>
public class CanonicalVectorTests
{
    private static readonly string VectorDirectory =
        Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "canonical");

    [Theory]
    [InlineData("01-create-shaped.json")]
    [InlineData("02-update-shaped.json")]
    [InlineData("03-wire-evidence-card-request.json")]
    [InlineData("04-wire-evidence-card-request-amended.json")]
    [InlineData("05-key-order-stress.json")]
    [InlineData("06-number-forms.json")]
    [InlineData("07-money-and-escapes.json")]
    public void TheVectorsBytesAndDigestAreReproduced(string file)
    {
        var vector = Load(file);
        var accepted = AcceptedState(vector);

        var canonical = CanonicalSerializer.CanonicalString(accepted);
        Assert.Equal(vector.ExpectedBytesUtf8, canonical);

        // The bytes are the contract; the string above is the same document one encoding step
        // earlier, and this asserts the encoding step too.
        Assert.Equal(
            Encoding.UTF8.GetBytes(vector.ExpectedBytesUtf8),
            CanonicalSerializer.Canonicalize(accepted));

        Assert.Equal(vector.ExpectedSha256, CanonicalSerializer.CanonicalHash(accepted));
    }

    /// <summary>
    /// The amended vector's hash differs from the unamended one's — the substitution SR-1 exists to
    /// prevent, made checkable.
    /// </summary>
    [Fact]
    public void AnAmendedProposalHashesDifferentlyFromTheProposalItAmends()
    {
        var proposal = Load("03-wire-evidence-card-request.json");
        var amended = Load("04-wire-evidence-card-request-amended.json");

        Assert.NotEqual(proposal.ExpectedSha256, amended.ExpectedSha256);
        Assert.Equal(
            CanonicalSerializer.CanonicalHash(AcceptedState(proposal)),
            proposal.ExpectedSha256);
        Assert.Equal(
            CanonicalSerializer.CanonicalHash(AcceptedState(amended)),
            amended.ExpectedSha256);
    }

    /// <summary>
    /// The typed path and the document path agree about the same amendment.
    ///
    /// <para>
    /// The vectors are canonicalized as <b>documents</b>, not through the model — see
    /// <see cref="AcceptedState"/> for why that is forced rather than chosen. This test closes the
    /// gap that leaves: a record built through <c>Affidavit</c>, amended through
    /// <see cref="AffidavitAmendments.Apply"/> and hashed through the typed overload produces the
    /// same bytes as the same record amended as a document. If the two mint sites ever drifted, the
    /// row a decision writes and the hash a grant binds to would disagree about that decision.
    /// </para>
    /// </summary>
    [Fact]
    public void TheTypedPathAndTheDocumentPathAgreeAboutAnAmendment()
    {
        var entryId = Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001");
        var decisionAt = new DateTimeOffset(2026, 9, 4, 9, 12, 0, TimeSpan.Zero);

        var proposal = Affidavit.Create(
            "WriteUpdate",
            "Widget",
            "W-1",
            [
                new AffidavitField(
                    "Status",
                    "Active",
                    null,
                    ProvenanceChain.From(ProvenanceTag.FromUser("Status", binding: null)),
                    IsMandatory: true,
                    Kind: AffidavitFieldKind.Enum,
                    AllowedValues: ["Active", "Retired"]),
                new AffidavitField(
                    "Weight",
                    12.5,
                    10.0,
                    ProvenanceChain.From(ProvenanceTag.FromTool("search_widget")),
                    IsMandatory: false,
                    Kind: AffidavitFieldKind.Number),
            ],
            warnings: []);

        var amendments = new Dictionary<string, object?> { ["Status"] = "Retired", ["Weight"] = null };

        var throughTheModel = CanonicalSerializer.CanonicalString(
            proposal, amendments, entryId, decisionAt, "ana");

        var throughTheDocument = CanonicalSerializer.CanonicalString(
            CanonicalSerializer.ApplyAmendmentsForCanonical(
                (JsonObject)CanonicalSerializer.ToDocument(proposal),
                amendments,
                entryId,
                decisionAt,
                "ana"));

        Assert.Equal(throughTheModel, throughTheDocument);
    }

    /// <summary>
    /// The accepted state a vector's canonical form is taken over: the amended document where the
    /// vector carries amendments, the input as filed otherwise.
    ///
    /// <para>
    /// <b>Why this goes through the document overload rather than through <c>Affidavit</c>.</b> Two of
    /// the seven inputs are not Affidavits at all, so no typed path could read them. And the amended
    /// vector settles it for the other five: its expected bytes keep the <b>seed</b> spelling
    /// <c>evidence</c> on the tag the reviewer superseded, while the tag the reviewer's act put in
    /// force carries the v0.1 spelling <c>note</c> and an <c>at</c>. Both are correct — a canonical
    /// form re-writes nothing it was handed, and only the newly minted tag is this implementation's
    /// to spell — but that is reachable only by folding the amendment into the document. Re-typing
    /// the input through the model first would silently rename a property on a tag nobody amended and
    /// produce different bytes for a document nobody changed. The typed path is covered by
    /// <see cref="TheTypedPathAndTheDocumentPathAgreeAboutAnAmendment"/>.
    /// </para>
    /// </summary>
    private static JsonNode AcceptedState(Vector vector) =>
        vector.Amendments is null
            ? vector.Input
            : CanonicalSerializer.ApplyAmendmentsForCanonical(
                vector.Input,
                vector.Amendments,
                vector.ReviewerAct!.EntryId,
                vector.ReviewerAct.DecisionAt,
                vector.ReviewerAct.By);

    private static Vector Load(string file)
    {
        var path = Path.Combine(VectorDirectory, file);
        Assert.True(File.Exists(path), $"The vendored vector {file} is missing from {VectorDirectory}.");

        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Dictionary<string, object?>? amendments = null;
        if (document["amendments"] is JsonObject map)
        {
            amendments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in map)
            {
                amendments[name] = value is null
                    ? null
                    : JsonSerializer.Deserialize<object?>(value.ToJsonString());
            }
        }

        ReviewerAct? act = null;
        if (document["reviewerAct"] is JsonObject reviewer)
        {
            act = new ReviewerAct(
                Guid.Parse(reviewer["entryId"]!.GetValue<string>()),
                DateTimeOffset.Parse(
                    reviewer["decisionAt"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture),
                reviewer["by"]!.GetValue<string>());
        }

        return new Vector(
            document["id"]!.GetValue<string>(),
            document["input"]!.AsObject(),
            amendments,
            act,
            document["expectedBytesUtf8"]!.GetValue<string>(),
            document["expectedSha256"]!.GetValue<string>());
    }

    private sealed record Vector(
        string Id,
        JsonObject Input,
        Dictionary<string, object?>? Amendments,
        ReviewerAct? ReviewerAct,
        string ExpectedBytesUtf8,
        string ExpectedSha256);

    private sealed record ReviewerAct(Guid EntryId, DateTimeOffset DecisionAt, string By);
}
