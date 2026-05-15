namespace Affiant.Abstractions.Tests.Models;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Xunit;

public class AffidavitEmittedEventTests
{
    private static AffidavitEmittedEvent SampleEvent() => new(
        ConversationId: "conv-123",
        AffidavitId: Guid.Parse("00000000-0000-0000-0000-000000000001"),
        OperationType: "WriteCreate",
        EntityType: "Thing",
        PopulatedFieldCount: 3,
        AggregateConfidence: 0.85f,
        EmptyProvenanceFieldCount: 0);

    [Fact]
    public void RecordEquality_HoldsForIdenticalFields()
    {
        var a = SampleEvent();
        var b = SampleEvent();
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RecordEquality_FailsWhenAnyFieldDiffers()
    {
        var a = SampleEvent();
        Assert.NotEqual(a, a with { ConversationId = "different" });
        Assert.NotEqual(a, a with { AffidavitId = Guid.NewGuid() });
        Assert.NotEqual(a, a with { OperationType = "WriteUpdate" });
        Assert.NotEqual(a, a with { EntityType = "OtherThing" });
        Assert.NotEqual(a, a with { PopulatedFieldCount = 0 });
        Assert.NotEqual(a, a with { AggregateConfidence = 0f });
        Assert.NotEqual(a, a with { EmptyProvenanceFieldCount = 1 });
    }

    [Fact]
    public void JsonRoundTrip_PreservesAllSevenFields()
    {
        var original = SampleEvent();
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AffidavitEmittedEvent>(json);
        Assert.Equal(original, deserialized);
    }
}
