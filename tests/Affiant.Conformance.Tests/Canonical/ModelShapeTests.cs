using Xunit;
using System.Text.Json.Nodes;
using Affiant.Conformance.Tests.Model;

namespace Affiant.Conformance.Tests.Canonical;

/// <summary>
/// A canonical vector row says whether the shipped model can HOLD the shape the rulebook pins, so
/// the set of properties it is measured against has to be the set the shipped records actually
/// carry. A hand-maintained list is a claim about a release that goes stale the moment a record
/// gains a property, and a stale one puts a false sentence — "the record has no such property" —
/// into a published parity manifest.
/// </summary>
public class ModelShapeTests
{
    private static CanonicalVector VectorOver(JsonObject input) => new(
        "test/model-shape",
        ["SR-1"],
        "a shape check, not a byte check",
        input,
        Amendments: null,
        ReviewerAct: null,
        ExpectedBytesUtf8: "",
        ExpectedSha256: "",
        SourcePath: "(in memory)");

    private static JsonObject AffidavitShaped(JsonObject affidavitExtras, JsonObject tagExtras)
    {
        var tag = new JsonObject
        {
            ["source"] = "UserStated",
            ["confidence"] = 1.0,
        };
        foreach (var (key, value) in tagExtras) tag[key] = value?.DeepClone();

        var input = new JsonObject
        {
            ["operationType"] = "update",
            ["entityType"] = "Invoice",
            ["entityId"] = "invoice-1",
            ["fields"] = new JsonArray(new JsonObject
            {
                ["name"] = "status",
                ["value"] = "Active",
                ["previousValue"] = "Draft",
                ["provenance"] = new JsonObject { ["current"] = tag },
            }),
            ["aggregateConfidence"] = 1.0,
            ["warnings"] = new JsonArray(),
            ["requiresConfirmation"] = true,
        };
        foreach (var (key, value) in affidavitExtras) input[key] = value?.DeepClone();
        return input;
    }

    [Theory]
    [InlineData("populatedConfidence")]
    [InlineData("emptyFieldCount")]
    [InlineData("protocolVersion")]
    [InlineData("conversationTurn")]
    [InlineData("createdAt")]
    public void APropertyTheAffidavitRecordCarries_IsNotReportedAbsent(string property)
    {
        var input = AffidavitShaped(new JsonObject { [property] = 1 }, []);

        var (_, diff, _) = CanonicalVectorRunner.Run(VectorOver(input));

        Assert.DoesNotContain(diff, d => d.At == $"model.{property}");
    }

    [Theory]
    [InlineData("note", "read back to the member")]
    [InlineData("at", "2026-09-04T09:00:00.000Z")]
    public void APropertyTheProvenanceTagRecordCarries_IsNotReportedAbsent(string property, string value)
    {
        var input = AffidavitShaped([], new JsonObject { [property] = value });

        var (_, diff, _) = CanonicalVectorRunner.Run(VectorOver(input));

        Assert.DoesNotContain(diff, d => d.At == $"model.fields[0].provenance.current.{property}");
    }

    [Fact]
    public void APropertyNoRecordCarries_IsStillReportedAbsent()
    {
        var input = AffidavitShaped(new JsonObject { ["settledAt"] = "2026-09-04T09:00:00.000Z" }, []);

        var (_, diff, _) = CanonicalVectorRunner.Run(VectorOver(input));

        Assert.Contains(diff, d => d.At == "model.settledAt");
    }
}
