namespace Affiant.Core.Tests.Serialization;

using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Core.Serialization;
using Xunit;

/// <summary>
/// Which conversation turn the minted reviewer-act tag carries — the record's, never the amended
/// field's, on both mint paths.
/// </summary>
/// <remarks>
/// The rulebook's own answer is in the normative vector
/// <c>canonical/wire-evidence-card-request-amended</c> at <c>v0.1.1</c>: its input's <c>Status</c>
/// field carries <c>conversationTurn: null</c> on the tag in force, the Affidavit record carries
/// <c>conversationTurn: 3</c>, and the amended state's minted tag carries <b>3</b>. The turn is the
/// RECORD's, not the amended field's.
/// </remarks>
public sealed class AmendmentTurnTests
{
    private const string VectorInput = """
    {
      "protocolVersion": "0.1.0",
      "operationType": "update",
      "entityType": "Widget",
      "entityId": "W-1",
      "createdAt": "2026-09-04T09:00:00.000Z",
      "conversationTurn": 3,
      "aggregateConfidence": 1,
      "populatedConfidence": 1,
      "emptyFieldCount": 0,
      "fields": [
        {
          "name": "Status",
          "kind": "enum",
          "isMandatory": true,
          "value": "Active",
          "previousValue": null,
          "provenance": {
            "current": {
              "source": "UserStated",
              "confidence": 1,
              "note": "User stated: Status",
              "conversationTurn": null,
              "binding": null,
              "at": "2026-09-04T09:00:00.000Z"
            },
            "prior": []
          }
        }
      ]
    }
    """;

    /// <summary>The canonical path agrees with the rulebook: the tag carries the record's turn.</summary>
    [Fact]
    public void CanonicalPath_CarriesTheRecordsTurn()
    {
        var doc = (JsonObject)JsonNode.Parse(VectorInput)!;
        var amended = CanonicalSerializer.ApplyAmendmentsForCanonical(
            doc,
            new Dictionary<string, object?> { ["Status"] = "Retired" },
            Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"),
            DateTimeOffset.Parse("2026-09-04T09:12:00.000Z"),
            "ana");

        var turn = amended["fields"]![0]!["provenance"]!["current"]!["conversationTurn"];
        Assert.Equal(3, turn!.GetValue<int>());
    }

    /// <summary>
    /// The typed path is handed the same facts and mints the AMENDED FIELD's turn instead — null
    /// here, where the rulebook's vector says 3. The two paths disagree about the same decision.
    /// </summary>
    [Fact]
    public void TypedPath_CarriesTheRecordsTurnToo()
    {
        var field = new AffidavitField(
            Name: "Status",
            Value: "Active",
            PreviousValue: null,
            Provenance: ProvenanceChain.From(new ProvenanceTag(
                ProvenanceSource.UserStated, 1.0f, "User stated: Status",
                ConversationTurn: null, Binding: null,
                At: DateTimeOffset.Parse("2026-09-04T09:00:00.000Z"))),
            IsMandatory: true);

        var affidavit = Affidavit.Create(
            "update", "Widget", "W-1", [field], [],
            conversationTurn: 3,
            createdAt: DateTimeOffset.Parse("2026-09-04T09:00:00.000Z"));

        var amended = AffidavitAmendments.Apply(
            affidavit,
            new Dictionary<string, object?> { ["Status"] = "Retired" },
            Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"),
            DateTimeOffset.Parse("2026-09-04T09:12:00.000Z"),
            "ana");

        // The rulebook's answer, pinned: the record's turn (3), which the vector states.
        Assert.Equal(3, amended.Fields[0].Provenance.Current.ConversationTurn);
    }

    /// <summary>
    /// The in-code claim at CanonicalSerializer.cs:240-241 — "The typed path reads
    /// Affidavit.ConversationTurn for the same reason, and the two must not drift" — is testable:
    /// the two paths, given the same decision, must mint the same turn.
    /// </summary>
    [Fact]
    public void TheTwoPathsDoNotDrift()
    {
        var doc = (JsonObject)JsonNode.Parse(VectorInput)!;
        var canonical = CanonicalSerializer.ApplyAmendmentsForCanonical(
            doc,
            new Dictionary<string, object?> { ["Status"] = "Retired" },
            Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"),
            DateTimeOffset.Parse("2026-09-04T09:12:00.000Z"),
            "ana");
        var canonicalTurn = canonical["fields"]![0]!["provenance"]!["current"]!["conversationTurn"]
            ?.GetValue<int>();

        var field = new AffidavitField(
            Name: "Status",
            Value: "Active",
            PreviousValue: null,
            Provenance: ProvenanceChain.From(new ProvenanceTag(
                ProvenanceSource.UserStated, 1.0f, "User stated: Status",
                ConversationTurn: null, Binding: null,
                At: DateTimeOffset.Parse("2026-09-04T09:00:00.000Z"))),
            IsMandatory: true);
        var typed = AffidavitAmendments.Apply(
            Affidavit.Create(
                "update", "Widget", "W-1", [field], [],
                conversationTurn: 3,
                createdAt: DateTimeOffset.Parse("2026-09-04T09:00:00.000Z")),
            new Dictionary<string, object?> { ["Status"] = "Retired" },
            Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"),
            DateTimeOffset.Parse("2026-09-04T09:12:00.000Z"),
            "ana");

        Assert.Equal(canonicalTurn, typed.Fields[0].Provenance.Current.ConversationTurn);
    }

    /// <summary>
    /// The record can state what the rulebook's record states. Without these three there is nowhere
    /// for the turn the tag must carry to come from, and the canonical form of a .NET record could
    /// never be the canonical form the rule defines.
    /// </summary>
    [Fact]
    public void TheAffidavitRecordStatesTheThreeThingsTheRuleRequires()
    {
        Assert.NotNull(typeof(Affidavit).GetProperty("ConversationTurn"));
        Assert.NotNull(typeof(Affidavit).GetProperty("CreatedAt"));
        Assert.NotNull(typeof(Affidavit).GetProperty("ProtocolVersion"));

        var affidavit = Affidavit.Create(
            "update", "Widget", "W-1", [], [],
            conversationTurn: 3,
            createdAt: DateTimeOffset.Parse("2026-09-04T09:00:00.000Z"));

        var document = JsonNode.Parse(
            JsonSerializer.Serialize(affidavit, Affiant.Abstractions.Serialization.AffiantJson.SerializerOptions))!;

        Assert.Equal(Affiant.Abstractions.AffiantProtocol.Version, document["protocolVersion"]!.GetValue<string>());
        Assert.Equal(3, document["conversationTurn"]!.GetValue<int>());
        Assert.Equal("2026-09-04T09:00:00.000Z", document["createdAt"]!.GetValue<string>());
    }
}
