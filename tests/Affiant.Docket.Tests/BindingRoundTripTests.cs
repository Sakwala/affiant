namespace Affiant.Docket.Tests;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// A record that has been through a store reads back as the record that went in — bindings
/// included. A reviewer's act is the strongest claim a tag can carry (PV-2), and a store that
/// cannot read one back turns every later read of that row into an exception.
/// </summary>
public sealed class BindingRoundTripTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void AReviewerActBinding_SurvivesTheStoresOwnSerializer()
    {
        var affidavit = Affidavit.Create(
            "WriteUpdate",
            "Invoice",
            "invoice-1",
            [
                new AffidavitField(
                    "amount",
                    "4000",
                    null,
                    ProvenanceChain.From(AffidavitAmendments.AmendmentTag(
                        cleared: false,
                        Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"),
                        DateTimeOffset.Parse("2026-09-04T09:12:00.000Z"),
                        "ana",
                        conversationTurn: null))),
            ],
            []);

        var json = JsonSerializer.Serialize(affidavit, CamelCase);
        output.WriteLine("store options: " + json);

        var read = JsonSerializer.Deserialize<Affidavit>(json, CamelCase);

        Assert.NotNull(read);
        Assert.IsType<ProvenanceBinding.ReviewerAct>(read!.Fields[0].Provenance.Current.Binding);

        var wire = JsonSerializer.Serialize(
            affidavit, Affiant.Abstractions.Serialization.AffiantJson.SerializerOptions);
        output.WriteLine("wire options: " + wire);

        // The store reads back a record whichever of the two wrote it: one serializer's output is
        // the other's input the moment a host swaps a store or a row outlives a release.
        var crossed = JsonSerializer.Deserialize<Affidavit>(wire, CamelCase);
        Assert.IsType<ProvenanceBinding.ReviewerAct>(crossed!.Fields[0].Provenance.Current.Binding);
    }

    /// <summary>
    /// PostgreSQL's <c>jsonb</c> does not preserve an object's key order: it sorts keys by length
    /// and then by bytes, so a binding written <c>{"kind":…,"ref":…}</c> comes back with <c>ref</c>
    /// first. A reader that required the discriminator to arrive first would find no discriminator
    /// at all, and every row carrying a reviewer's amendment would be unreadable from the moment it
    /// was stored.
    /// </summary>
    [Fact]
    public void ABindingWhoseKeysTheStoreReordered_StillReadsBack()
    {
        const string reordered = """
        {
          "ref": { "entryId": "8f14e45f-ceea-467e-bd76-000000000001", "decisionAt": "2026-09-04T09:12:00+00:00" },
          "kind": "reviewer-act"
        }
        """;

        var binding = JsonSerializer.Deserialize<ProvenanceBinding>(reordered, CamelCase);

        var act = Assert.IsType<ProvenanceBinding.ReviewerAct>(binding);
        Assert.Equal(Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"), act.Ref.EntryId);
    }

    [Fact]
    public void ABindingWithNoKind_IsRefusedRatherThanGuessed()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProvenanceBinding>("""{"ref":{"field":"amount"}}""", CamelCase));
    }
}
