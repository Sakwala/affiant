namespace Affiant.Core.Tests.Primitives;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// Verifies that every framework primitive survives JSON serialization (invariant R2).
/// </summary>
public class PrimitiveRoundTripTests
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    [Theory]
    [InlineData(ProvenanceSource.UserStated)]
    [InlineData(ProvenanceSource.External)]
    [InlineData(ProvenanceSource.Computed)]
    [InlineData(ProvenanceSource.Inferred)]
    [InlineData(ProvenanceSource.Empty)]
    public void ProvenanceSource_survives_json_roundtrip(ProvenanceSource source)
    {
        var json = JsonSerializer.Serialize(source, s_opts);
        var result = JsonSerializer.Deserialize<ProvenanceSource>(json, s_opts);
        Assert.Equal(source, result);
    }

    [Fact]
    public void ProvenanceTag_survives_json_roundtrip()
    {
        var original = new ProvenanceTag(
            Source: ProvenanceSource.UserStated,
            Confidence: 1.0f,
            Evidence: "User stated start date",
            ConversationTurn: 3);

        var json = JsonSerializer.Serialize(original, s_opts);
        var result = JsonSerializer.Deserialize<ProvenanceTag>(json, s_opts);

        Assert.NotNull(result);
        Assert.Equal(original.Source, result.Source);
        Assert.Equal(original.Confidence, result.Confidence);
        Assert.Equal(original.Evidence, result.Evidence);
        Assert.Equal(original.ConversationTurn, result.ConversationTurn);
    }

    [Fact]
    public void ProvenanceChain_survives_json_roundtrip()
    {
        var tag1 = new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, "user stated", null);
        var tag2 = new ProvenanceTag(ProvenanceSource.Inferred, 0.6f, "inferred", 2);
        var chain = ProvenanceChain.From(tag1).Append(tag2);

        var json = JsonSerializer.Serialize(chain, s_opts);
        var result = JsonSerializer.Deserialize<ProvenanceChain>(json, s_opts);

        Assert.NotNull(result);
        Assert.Equal(chain.Current.Source, result.Current.Source);
        Assert.Equal(chain.Current.Confidence, result.Current.Confidence);
        Assert.Equal(chain.Prior.Count, result.Prior.Count);
        Assert.Equal(chain.Prior[0].Source, result.Prior[0].Source);
    }

    [Fact]
    public void AffidavitField_survives_json_roundtrip()
    {
        var tag = ProvenanceTag.FromUser("StartDate", binding: null);
        var field = new AffidavitField(
            Name: "StartDate",
            Value: "2026-05-01",
            PreviousValue: null,
            Provenance: ProvenanceChain.From(tag));

        var json = JsonSerializer.Serialize(field, s_opts);
        var result = JsonSerializer.Deserialize<AffidavitField>(json, s_opts);

        Assert.NotNull(result);
        Assert.Equal(field.Name, result.Name);
        Assert.NotNull(result.Provenance);
        Assert.Equal(field.Provenance.Current.Source, result.Provenance.Current.Source);
    }

    [Fact]
    public void Affidavit_survives_json_roundtrip()
    {
        var tag = ProvenanceTag.FromUser("StartDate", binding: null);
        var affidavit = new Affidavit(
            OperationType: "RequestLeave",
            EntityType: "LeaveRequest",
            EntityId: null,
            Fields: new[]
            {
                new AffidavitField("StartDate", "2026-05-01", null, ProvenanceChain.From(tag)),
                new AffidavitField("EndDate", "2026-05-05", null, ProvenanceChain.From(ProvenanceTag.FromUser("EndDate", binding: null))),
            },
            AggregateConfidence: 1.0f,
            PopulatedConfidence: 1.0f,
            EmptyFieldCount: 0,
            Warnings: Array.Empty<string>(),
            RequiresConfirmation: true);

        var json = JsonSerializer.Serialize(affidavit, s_opts);
        var result = JsonSerializer.Deserialize<Affidavit>(json, s_opts);

        Assert.NotNull(result);
        Assert.Equal(affidavit.OperationType, result.OperationType);
        Assert.Equal(affidavit.EntityType, result.EntityType);
        Assert.Equal(affidavit.Fields.Length, result.Fields.Length);
        Assert.Equal(affidavit.RequiresConfirmation, result.RequiresConfirmation);
    }

    [Fact]
    public void EntityRef_survives_json_roundtrip()
    {
        var entityRef = new EntityRef(
            EntityType: "Employee",
            EntityId: "EMP-001",
            DisplayName: "Alice",
            Fields: new Dictionary<string, object>
            {
                { "Department", "Engineering" },
                { "ManagerId", "EMP-005" },
            });

        var json = JsonSerializer.Serialize(entityRef, s_opts);
        var result = JsonSerializer.Deserialize<EntityRef>(json, s_opts);

        Assert.NotNull(result);
        Assert.Equal(entityRef.EntityId, result.EntityId);
        Assert.Equal(entityRef.EntityType, result.EntityType);
        Assert.Equal(entityRef.Fields.Count, result.Fields.Count);
    }
}
