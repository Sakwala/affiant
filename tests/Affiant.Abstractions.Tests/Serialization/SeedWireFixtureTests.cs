namespace Affiant.Abstractions.Tests.Serialization;

using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Xunit;

/// <summary>
/// The <c>0.0.1-seed</c> wire examples, re-checked against what this release actually serializes.
///
/// <para>
/// These files are the payload shapes the two demo hosts assert against today, captured from a
/// running <c>1.0.0-beta.1</c>. This suite is not about whether they still validate — they are not
/// schemas — but about whether a host reading them would still find what it reads. Every difference
/// is asserted <b>by name</b>: a property that appeared, one that disappeared, one that was renamed.
/// A change nobody meant to make fails here rather than in a host's browser.
/// </para>
///
/// <para>
/// Two of the eight seed files — <c>action-decision-result</c> and <c>session-rehydrated</c> — are
/// host hub payloads that no framework type produces (SR-5: the transport is not the protocol), and
/// two more — <c>guide-ui</c> and <c>system-notification</c> — are host and UI shapes the framework
/// does carry types for but that no protocol rule constrains. The last two are asserted unchanged,
/// which is the point: this change touched neither.
/// </para>
/// </summary>
public class SeedWireFixtureTests
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "wire");

    // ── Unchanged ────────────────────────────────────────────────────────────

    [Fact]
    public void ASystemNotificationIsUnchanged() =>
        Assert.Equal(
            Seed("system-notification"),
            V01SchemaValidation.Wire(new SystemNotificationPayload("warning", "Session expiring soon")).ToJsonString());

    [Fact]
    public void AGuidanceWalkthroughIsUnchanged() =>
        Assert.Equal(
            Seed("guide-ui"),
            V01SchemaValidation.Wire(new UiGuidancePayload(
                "/work-orders/new",
                [
                    new UiGuidanceStep(
                        "aircraft-select",
                        "Select the aircraft",
                        "Choose the tail number this work order applies to.",
                        "N12345",
                        "bottom",
                        8),
                    new UiGuidanceStep(
                        "title-input",
                        "Confirm the title",
                        "Review the auto-generated title before submitting.",
                        null,
                        "top",
                        null),
                ],
                "Pre-filled from the conversation: aircraft N12345, A-Check inspection.")).ToJsonString());

    // ── Changed, and by exactly this much ────────────────────────────────────

    [Fact]
    public void AnExpiryNotificationGainsTheProtocolVersionAndTheKindAndNothingElse()
    {
        AssertDelta(
            "docket-expiring",
            V01SchemaValidation.Wire(new DocketExpiringNotification(
                Guid.Parse("8f14e45f-ceea-467e-bd76-000000000002"),
                new DateTimeOffset(2026, 8, 1, 0, 5, 0, TimeSpan.Zero))),
            added: ["protocolVersion", "kind"],
            removed: []);

        AssertDelta(
            "docket-expired",
            V01SchemaValidation.Wire(new DocketExpiredNotification(
                Guid.Parse("8f14e45f-ceea-467e-bd76-000000000003"))),
            added: ["protocolVersion", "kind"],
            removed: []);
    }

    /// <summary>
    /// An instant is spelled <c>2026-08-01T00:05:00.000Z</c> where the seed spelled it
    /// <c>2026-08-01T00:05:00+00:00</c>. The same instant, a different string: the framework now
    /// writes one spelling everywhere, because a canonical form (SR-1) is a byte sequence and two
    /// spellings of one instant are two hashes of one record. Every JavaScript client parses both.
    /// </summary>
    [Fact]
    public void AnInstantIsSpelledDifferentlyFromTheSeed()
    {
        var seed = JsonNode.Parse(Seed("docket-expiring"))!;
        var now = V01SchemaValidation.Wire(new DocketExpiringNotification(
            Guid.Parse("8f14e45f-ceea-467e-bd76-000000000002"),
            new DateTimeOffset(2026, 8, 1, 0, 5, 0, TimeSpan.Zero)));

        Assert.Equal("2026-08-01T00:05:00+00:00", seed["expiresAt"]!.GetValue<string>());
        Assert.Equal("2026-08-01T00:05:00.000Z", now["expiresAt"]!.GetValue<string>());

        Assert.Equal(
            DateTimeOffset.Parse(seed["expiresAt"]!.GetValue<string>(), null),
            DateTimeOffset.Parse(now["expiresAt"]!.GetValue<string>(), null));
    }

    [Fact]
    public void AnEvidenceCardGainsTheEnvelopesNewPropertiesAndLosesNone()
    {
        AssertDelta(
            "evidence-card-request",
            V01SchemaValidation.Wire(Card()),
            added:
            [
                "protocolVersion",     // SR-4
                "populatedConfidence", // AF-2, repeated from the record where the seed put it
                "emptyFieldCount",     // AF-2
                "requiresConfirmation",// the policy chain's verdict, not a property of the evidence
                "blocked",             // AZ-4 — null until the Docket row carries it
                "presentation",        // the rendering hints, lifted off the fields
                "hostOperation",       // the host's own verb, beside the protocol's shape
            ],
            removed: []);
    }

    [Fact]
    public void TheRecordInsideACardGainsTheCompanionNumbersAndTheThreeTheRuleRequires() =>
        AssertDelta(
            "evidence-card-request",
            V01SchemaValidation.Wire(Card()),
            at: "affidavit",
            added:
            [
                "populatedConfidence",  // AF-2
                "emptyFieldCount",      // AF-2
                "protocolVersion",      // SR-3 — a record is an envelope and says which version it speaks
                "conversationTurn",     // the turn the proposal was made on; what an amendment is dated to
                "createdAt",            // when the record was built, stamped by the gate's own clock
            ],
            removed: []);

    /// <summary>
    /// A provenance tag on the wire: <c>evidence</c> is now <c>note</c>, and a tag gained an
    /// <c>at</c> and a <c>binding</c>.
    ///
    /// <para>
    /// The rename is the one a host client actually has to act on. The whole record is the evidence;
    /// this property is the one sentence a person reads, which is what <c>note</c> says. A client
    /// rendering <c>tag.evidence</c> renders nothing after the upgrade — see the CHANGELOG's upgrade
    /// note.
    /// </para>
    /// </summary>
    [Fact]
    public void AProvenanceTagRenamesEvidenceToNoteAndGainsAnInstantAndABinding() =>
        AssertDelta(
            "evidence-card-request",
            V01SchemaValidation.Wire(Card()),
            at: "affidavit/fields/0/provenance/current",
            added: ["note", "at", "binding"],
            removed: ["evidence"]);

    [Fact]
    public void AResubmissionStillCarriesThePriorAmendmentsAReviewerAlreadyMade()
    {
        var seed = JsonNode.Parse(Seed("evidence-card-request-resubmission"))!;
        var prior = new Dictionary<string, object?> { ["Status"] = "Retired", ["Weight"] = 15.0 };

        var card = V01SchemaValidation.Wire(EvidenceCardRequest.For(
            Guid.Parse("8f14e45f-ceea-467e-bd76-000000000005"),
            SeedRecord(),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            prior));

        var before = seed["priorAmendments"]!.AsObject();
        var after = card["priorAmendments"]!.AsObject();

        Assert.Equal(before.Select(p => p.Key), after.Select(p => p.Key));
        Assert.Equal(before["Status"]!.GetValue<string>(), after["Status"]!.GetValue<string>());

        // 15.0 in the seed file and 15 here are the same number: the seed keeps the literal its
        // source wrote, and a value that arrives through the model keeps the shortest spelling of
        // the same double. A canonical form has one spelling per value (SR-1), and this is it.
        Assert.Equal(before["Weight"]!.GetValue<double>(), after["Weight"]!.GetValue<double>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Assert that the property names at <paramref name="at"/> differ from the seed's by exactly
    /// <paramref name="added"/> and <paramref name="removed"/> — no more, and no fewer.
    /// </summary>
    private static void AssertDelta(
        string fixture,
        JsonNode now,
        string[] added,
        string[] removed,
        string at = "")
    {
        var before = Keys(JsonNode.Parse(Seed(fixture))!, at);
        var after = Keys(now, at);

        Assert.Equal([.. added.Order(StringComparer.Ordinal)], After(after.Except(before)));
        Assert.Equal([.. removed.Order(StringComparer.Ordinal)], After(before.Except(after)));

        static string[] After(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];
    }

    private static HashSet<string> Keys(JsonNode document, string pointer)
    {
        var node = document;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = int.TryParse(segment, out var index) ? node![index] : node![segment];
        }

        return [.. node!.AsObject().Select(property => property.Key)];
    }

    private static string Seed(string fixture)
    {
        var path = Path.Combine(FixtureDirectory, $"{fixture}.json");
        Assert.True(File.Exists(path), $"The vendored seed fixture {fixture}.json is missing.");

        // Re-serialized so the comparison is about content rather than about a file's indentation.
        return JsonNode.Parse(File.ReadAllText(path))!.ToJsonString();
    }

    private static EvidenceCardRequest Card() =>
        EvidenceCardRequest.For(
            Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"),
            SeedRecord(),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            hostOperation: "WriteUpdate");

    /// <summary>The record the seed's Evidence Card carries, rebuilt through today's model.</summary>
    private static Affidavit SeedRecord() =>
        Affidavit.Create(
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
                    Kind: AffidavitFieldKind.Number,
                    Pattern: @"^\d+(\.\d+)?$"),
            ],
            warnings: []);
}
