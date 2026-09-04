namespace Affiant.Abstractions.Tests.Serialization;

using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Serialization;
using Affiant.Abstractions.Transport;
using Xunit;

/// <summary>
/// Every envelope the framework serializes, evaluated against the vendored v0.1 schema that defines
/// it (SR-3's "unknown properties rejected on core objects", SR-4's <c>protocolVersion</c>, AF-5's
/// discriminators).
///
/// <para>
/// <b>Why these tests assert a residue rather than only <c>IsValid</c>.</b> Two of the v0.1 schemas
/// describe records this release cannot yet produce, for reasons that belong to other changes: a
/// provenance tag's <c>at</c> is required and non-null, and stamping one everywhere means the
/// injected clock this change is not stacked on; a decision result's <c>attestation</c> is required,
/// and an attestation is the authorization change's to define. A test that only asserted validity
/// would have to be deleted and rewritten when those land. A test that asserts <b>the exact set of
/// remaining disagreements</b> instead does three things at once: it proves nothing else is wrong,
/// it names what is left and why in one place, and it turns green on its own — by failing loudly
/// with a shorter list — the moment another change closes one of them.
/// </para>
/// </summary>
public class WireSchemaTests
{
    private static readonly Guid DocketId = Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001");
    private static readonly DateTimeOffset RequiredBy = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    // ── The envelopes that conform outright ──────────────────────────────────

    [Fact]
    public void MoneyConformsToItsSchema() =>
        Assert.Empty(V01SchemaValidation.Violations(new Money("4000.10", "GBP"), "money"));

    [Fact]
    public void ABlockedMarkerConformsToItsSchema()
    {
        Assert.Empty(V01SchemaValidation.Violations(
            new BlockedMarker.RequirementNotImplemented(ReviewRequirement.MultiParty), "blocked"));

        Assert.Empty(V01SchemaValidation.Violations(
            new BlockedMarker.CoverageRefused(CoverageCategory.ProviderExecuted, "SendInvoice"), "blocked"));
    }

    [Theory]
    [MemberData(nameof(Notifications))]
    public void ANotificationConformsToItsSchema(string kind, DocketNotification notification)
    {
        Assert.Empty(V01SchemaValidation.Violations(notification, "notification"));

        var wire = V01SchemaValidation.Wire(notification);
        Assert.Equal(AffiantProtocol.Version, wire["protocolVersion"]!.GetValue<string>());
        Assert.Equal(kind, wire["kind"]!.GetValue<string>());
    }

    public static TheoryData<string, DocketNotification> Notifications() => new()
    {
        { DocketNotificationKind.DocketExpiring, new DocketExpiringNotification(DocketId, RequiredBy) },
        { DocketNotificationKind.DocketExpired, new DocketExpiredNotification(DocketId) },
        {
            DocketNotificationKind.DocketTransition,
            new DocketTransitionNotification(
                DocketId, ReviewStatus.Pending, ReviewStatus.Approved, ExecutionOutcome.Unexecuted)
        },
    };

    /// <summary>
    /// AF-5's discriminator, on all three arms: <c>kind</c>, carrying <c>read</c>, <c>write</c> and
    /// <c>error</c>.
    ///
    /// <para>
    /// Only the discriminator is asserted against the schema's vocabulary, not the whole envelope.
    /// The framework's <see cref="ToolEnvelope"/> carries a <c>toolName</c> and a <c>timestamp</c> on
    /// every arm, and names a read tool's payload <c>summary</c>/<c>markdown</c>/<c>entities</c>
    /// where the protocol's read arm has a single opaque <c>result</c>; the protocol's shape and this
    /// framework's are the same union with different cargo. Reshaping the envelope is not this
    /// change's to make — SR-5 keeps the host-facing tool surface out of the protocol, and AF-5 asks
    /// for one discriminator on one union, which is what this closes.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryToolResultArmCarriesTheKindDiscriminator()
    {
        var read = V01SchemaValidation.Wire(
            (ToolEnvelope)new ReadResult("Search", RequiredBy, "s", "m", []));

        Assert.Equal("read", read["kind"]!.GetValue<string>());

        var error = V01SchemaValidation.Wire(
            (ToolEnvelope)new ToolError("Search", RequiredBy, "review-filing-failed", "no", false));

        Assert.Equal("error", error["kind"]!.GetValue<string>());

        var write = V01SchemaValidation.Wire(
            (ToolEnvelope)new WriteProposal("CreateWidget", RequiredBy, new { }));

        Assert.Equal("write", write["kind"]!.GetValue<string>());
    }

    // ── The card, and the residue it still carries ───────────────────────────

    /// <summary>
    /// The Evidence Card envelope's own shape conforms: it carries the protocol version, the entry,
    /// the deadline, the prior amendments, the two companion confidence numbers, the blocked marker
    /// and the confirmation verdict, plus the presentation, warnings and host verb when it has them —
    /// and nothing the schema does not admit.
    /// </summary>
    [Fact]
    public void TheCardEnvelopeConformsExceptWhereAnotherChangeOwnsTheGap()
    {
        var card = EvidenceCardRequest.For(
            DocketId, Proposal(), RequiredBy, hostOperation: "Reprice");

        Assert.Equal(
            ExpectedResidue,
            V01SchemaValidation.Violations(card, "evidence-card-request"));
    }

    [Fact]
    public void TheCardCarriesEverythingTheEnvelopeAdds()
    {
        var wire = V01SchemaValidation.Wire(
            EvidenceCardRequest.For(DocketId, Proposal(), RequiredBy, hostOperation: "Reprice"));

        Assert.Equal(AffiantProtocol.Version, wire["protocolVersion"]!.GetValue<string>());
        Assert.Equal(0.9, wire["populatedConfidence"]!.GetValue<double>(), 6);
        Assert.Equal(1, wire["emptyFieldCount"]!.GetValue<int>());
        Assert.True(wire["requiresConfirmation"]!.GetValue<bool>());
        Assert.Equal("Reprice", wire["hostOperation"]!.GetValue<string>());
        Assert.True(wire.AsObject().ContainsKey("blocked"));
        Assert.Null(wire["blocked"]);

        // presentation is lifted from the field metadata the strategies declare.
        var presentation = wire["presentation"]!.AsArray();
        var status = Assert.Single(presentation, p => p!["name"]!.GetValue<string>() == "Status")!;
        Assert.Equal("enum", status["kind"]!.GetValue<string>());
        Assert.Equal(["Active", "Retired"], status["allowedValues"]!.AsArray().Select(v => v!.GetValue<string>()));

        Assert.Equal(["The total changed by more than 10x."], wire["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
    }

    /// <summary>
    /// An optional property with nothing to say is <b>omitted</b>, not written null (SR-1, and the
    /// schema, which types <c>presentation</c> as an array and would reject a null).
    /// </summary>
    [Fact]
    public void AnOptionalEnvelopePropertyWithNothingToSayIsOmitted()
    {
        var bare = Affidavit.Create(
            "WriteCreate",
            "Widget",
            null,
            [new AffidavitField("Note", "hello", null, ProvenanceChain.From(Tag(0.5f)))],
            warnings: []);

        var wire = V01SchemaValidation.Wire(EvidenceCardRequest.For(DocketId, bare, RequiredBy)).AsObject();

        Assert.False(wire.ContainsKey("presentation"));
        Assert.False(wire.ContainsKey("warnings"));
        Assert.False(wire.ContainsKey("hostOperation"));

        // ...while a required-and-nullable property is written null rather than omitted, so a reader
        // never has to tell "nothing to report" from "the property was left off".
        Assert.True(wire.ContainsKey("blocked"));
        Assert.True(wire.ContainsKey("priorAmendments"));
        Assert.True(wire.ContainsKey("populatedConfidence"));
    }

    /// <summary>
    /// A blocked card never also claims a confirmation is being awaited — it must not offer a
    /// reviewer surface an approve button that cannot work (AZ-4).
    /// </summary>
    [Fact]
    public void ABlockedCardDoesNotAlsoRequireConfirmation()
    {
        var card = EvidenceCardRequest.For(
            DocketId,
            Proposal(),
            RequiredBy,
            blocked: new BlockedMarker.RequirementNotImplemented(ReviewRequirement.MultiParty));

        Assert.False(card.RequiresConfirmation);

        var wire = V01SchemaValidation.Wire(card);
        Assert.Equal("requirement-not-implemented", wire["blocked"]!["code"]!.GetValue<string>());
        Assert.Equal("MultiParty", wire["blocked"]!["level"]!.GetValue<string>());
    }

    // ── The decision result, and the property that waits on another change ───

    [Fact]
    public void TheDecisionResultCarriesTheProtocolVersionAndTheProtocolsOutcomeVocabulary()
    {
        var wire = V01SchemaValidation.Wire(
            DecisionResult.For(new ReviewOutcome.Approved(DocketId)));

        Assert.Equal(AffiantProtocol.Version, wire["protocolVersion"]!.GetValue<string>());
        Assert.Equal("approved", wire["outcome"]!.GetValue<string>());
        Assert.Equal("unexecuted", wire["execution"]!.GetValue<string>());
    }

    /// <summary>
    /// The one envelope whose only remaining disagreement is a required property another change
    /// owns: <c>attestation</c>, who agreed (AZ-1). See <see cref="DecisionResult"/> for why it is
    /// not invented here.
    /// </summary>
    [Fact]
    public void TheDecisionResultsOnlyGapIsTheAttestationAnotherChangeOwns() =>
        Assert.Equal(
            ["""/ :: required"""],
            V01SchemaValidation.Violations(
                DecisionResult.For(new ReviewOutcome.Rejected(DocketId)), "decision-result"));

    [Fact]
    public void AReferralHasNoProtocolOutcomeAndIsRefusedRatherThanMapped() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DecisionResult.For(new ReviewOutcome.Referral(DocketId, "finance")));

    // ── SR-3: the casing each schema freezes ─────────────────────────────────

    [Fact]
    public void ProvenanceSourcesArePascalCaseAndDocketStatusesAreLowercase()
    {
        var enums = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "enum-values.json")))!;

        var sources = enums["provenanceSource"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        foreach (var source in Enum.GetValues<ProvenanceSource>())
        {
            Assert.Contains(
                JsonSerializer.Serialize(source, AffiantJson.SerializerOptions).Trim('"'),
                sources);
        }

        Assert.Equal("\"pending\"", JsonSerializer.Serialize(ReviewStatus.Pending, AffiantJson.SerializerOptions));
        Assert.Equal("\"approved\"", JsonSerializer.Serialize(ReviewStatus.Approved, AffiantJson.SerializerOptions));

        // A requirement level stays PascalCase — that is how the blocked schema spells it.
        Assert.Equal(
            "\"ReferralRequired\"",
            JsonSerializer.Serialize(ReviewRequirement.ReferralRequired, AffiantJson.SerializerOptions));
    }

    [Fact]
    public void AnInstantIsWrittenUtcWithMillisecondsAndATrailingZ() =>
        Assert.Equal(
            "\"2026-08-01T00:05:00.000Z\"",
            JsonSerializer.Serialize(
                new DateTimeOffset(2026, 8, 1, 1, 5, 0, TimeSpan.FromHours(1)),
                AffiantJson.SerializerOptions));

    // ── The residue, named ───────────────────────────────────────────────────

    /// <summary>
    /// What the v0.1 schemas still say about a card this release produces, and who closes each one.
    ///
    /// <para>
    /// <b>The tag's instant</b> — <c>.../provenance/current/at :: type</c>. The v0.1 tag requires
    /// <c>at</c> and types it as a non-null instant. Stamping one at every mint site means reading a
    /// clock, and the framework's one time seam — an injected <c>TimeProvider</c> — arrives in a
    /// separate change this one is not stacked on; until then a tag carries <c>at: null</c>. The one
    /// tag minted with an instant already in hand, a reviewer's accepted amendment, carries it today
    /// (see the canonical-vector suite).
    /// </para>
    ///
    /// <para>
    /// <b>Everything else on this list is one finding, not four: the v0.1 Affidavit schema and the
    /// v0.1 canonical byte vectors describe different records, and no implementation can satisfy
    /// both.</b> The vectors are normative bytes an implementation must reproduce; the schema is a
    /// normative shape it must validate against; and they disagree in four places:
    /// <list type="bullet">
    /// <item><c>/affidavit/warnings</c> and <c>/affidavit/requiresConfirmation ::
    /// not-admitted</c> — the schema puts both on the card envelope and forbids them on the record.
    /// The card carries them in both places, so a consumer written against either shape finds
    /// them.</item>
    /// <item><c>/affidavit/fields/N/allowedValues</c> and <c>.../pattern :: not-admitted</c> — the
    /// same split for the two per-field rendering hints, which the schema moves to the envelope's
    /// <c>presentation</c> array. The card carries those in both places too.</item>
    /// <item><c>/affidavit/operationType :: const|enum|oneOf</c> — the schema's operation vocabulary
    /// is two-valued and shape-shaped, <c>"create"</c> and <c>"update"</c>, with the host's own verb
    /// travelling beside it. This framework's record spells it <c>"WriteCreate"</c> /
    /// <c>"WriteUpdate"</c>, its own four-valued vocabulary; the card's <c>hostOperation</c> is the
    /// beside-it half and is carried now.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The record's three required properties are no longer on this list: <c>protocolVersion</c>,
    /// <c>conversationTurn</c> and <c>createdAt</c> are properties of the record, which is what the
    /// rulebook's v0.1.1 vectors and its Affidavit schema both describe. What is left is one
    /// finding: this framework's record still carries four things the schema keeps on the envelope,
    /// and spells the operation in its own vocabulary.
    /// </para>
    /// </summary>
    private static readonly string[] ExpectedResidue =
    [
        "/affidavit/fields/0/allowedValues :: not-admitted",
        "/affidavit/fields/0/pattern :: not-admitted",
        "/affidavit/fields/0/provenance/current/at :: type",
        "/affidavit/fields/1/allowedValues :: not-admitted",
        "/affidavit/fields/1/pattern :: not-admitted",
        "/affidavit/fields/1/provenance/current/at :: type",
        "/affidavit/operationType :: const",
        "/affidavit/operationType :: enum",
        "/affidavit/operationType :: oneOf",
        "/affidavit/requiresConfirmation :: not-admitted",
        "/affidavit/warnings :: not-admitted",
    ];

    private static Affidavit Proposal() =>
        Affidavit.Create(
            "WriteUpdate",
            "Invoice",
            "INV-2026-0044",
            [
                new AffidavitField(
                    "Status",
                    "Active",
                    "Draft",
                    ProvenanceChain.From(Tag(0.9f)),
                    IsMandatory: true,
                    Kind: AffidavitFieldKind.Enum,
                    AllowedValues: ["Active", "Retired"]),
                new AffidavitField(
                    "Owner",
                    null,
                    null,
                    ProvenanceChain.From(ProvenanceTag.Empty),
                    IsMandatory: false,
                    Kind: AffidavitFieldKind.Text),
            ],
            warnings: ["The total changed by more than 10x."],
            conversationTurn: 1,
            createdAt: RequiredBy.AddMinutes(-30));

    private static ProvenanceTag Tag(float confidence) =>
        new(ProvenanceSource.Conversation, confidence, "Extracted from the turn", 1);
}
