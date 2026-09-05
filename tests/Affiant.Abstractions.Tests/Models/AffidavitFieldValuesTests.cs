namespace Affiant.Abstractions.Tests.Models;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// A record that has been through a store scores, compares and reads exactly as it did before it
/// was written.
/// </summary>
/// <remarks>
/// The defect: <see cref="AffidavitField.Value"/> is <c>object?</c>, so a round trip through any
/// store hands every field back as a raw JSON element rather than the number, string or boolean the
/// projection put there. A host risk scorer that pattern-matches on the value's type then sees an
/// unrecognised type for every field of every stored row and falls through to its default branch —
/// so the same content scores one way the first time it is filed and another way when it is
/// resubmitted, which is the one path that always reads the record back out of the store.
/// </remarks>
public class AffidavitFieldValuesTests
{
    [Fact]
    public void AStoredAffidavit_ComesBackWithTheTypesItWasFiledWith()
    {
        var filed = Affidavit.Create(
            operationType: "CreateOrder",
            entityType: "Order",
            entityId: null,
            fields:
            [
                Field("title", "Widget", AffidavitFieldKind.Text),
                Field("quantity", 42, AffidavitFieldKind.Number),
                Field("unitPrice", 19.95m, AffidavitFieldKind.Number),
                Field("expedited", true, AffidavitFieldKind.Text),
                Field("dueAt", "2026-09-04T09:00:00.0000000+00:00", AffidavitFieldKind.Date),
                Field("status", "open", AffidavitFieldKind.Enum),
            ],
            warnings: []);

        var readBack = AffidavitFieldValues.Typed(RoundTrip(filed));

        Assert.Equal("Widget", Value(readBack, "title"));
        Assert.Equal(42L, Value(readBack, "quantity"));
        Assert.Equal(19.95m, Value(readBack, "unitPrice"));
        Assert.Equal(true, Value(readBack, "expedited"));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero),
            Value(readBack, "dueAt"));
        Assert.Equal("open", Value(readBack, "status"));

        // The point of all of it: nothing a scorer sees is a JSON element.
        Assert.All(readBack.Fields, f => Assert.IsNotType<JsonElement>(f.Value));
    }

    [Fact]
    public void APreviousValue_IsReadBackTheSameWayTheProposedValueIs()
    {
        var filed = Affidavit.Create(
            operationType: "UpdateOrder",
            entityType: "Order",
            entityId: "order-1",
            fields: [Field("quantity", 7, AffidavitFieldKind.Number, previousValue: 3)],
            warnings: []);

        var readBack = AffidavitFieldValues.Typed(RoundTrip(filed));

        Assert.Equal(7L, readBack.Fields.Single().Value);
        Assert.Equal(3L, readBack.Fields.Single().PreviousValue);
    }

    [Fact]
    public void ANestedValue_IsReadBackAllTheWayDown()
    {
        var filed = Affidavit.Create(
            operationType: "CreateOrder",
            entityType: "Order",
            entityId: null,
            fields: [Field("lines", new object?[] { new Dictionary<string, object?> { ["sku"] = "A", ["qty"] = 2 } }, AffidavitFieldKind.Text)],
            warnings: []);

        var readBack = AffidavitFieldValues.Typed(RoundTrip(filed));

        var lines = Assert.IsType<object?[]>(readBack.Fields.Single().Value);
        var line = Assert.IsType<Dictionary<string, object?>>(lines.Single());
        Assert.Equal("A", line["sku"]);
        Assert.Equal(2L, line["qty"]);
    }

    [Fact]
    public void ADateFieldWhoseTextIsNotAnInstant_KeepsItsText()
    {
        // A rehydration is not a validation pass. Losing what was filed would be worse than handing
        // a scorer a string it can see is a string.
        var element = JsonSerializer.Deserialize<JsonElement>("\"tomorrow\"");
        Assert.Equal("tomorrow", AffidavitFieldValues.Typed(element, AffidavitFieldKind.Date));
    }

    [Fact]
    public void AnAffidavitThatNeverWentNearAStore_IsReturnedUntouched()
    {
        var filed = Affidavit.Create(
            operationType: "CreateOrder",
            entityType: "Order",
            entityId: null,
            fields: [Field("title", "Widget", AffidavitFieldKind.Text)],
            warnings: []);

        Assert.Same(filed, AffidavitFieldValues.Typed(filed));
    }

    [Fact]
    public void AnAmendmentMap_IsReadBackTyped_AndAClearedFieldStaysCleared()
    {
        var stored = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """{"quantity": 12, "note": null, "unitPrice": 4.5}""")!;

        var typed = AffidavitFieldValues.Typed(stored)!;

        Assert.Equal(12L, typed["quantity"]);
        Assert.Equal(4.5m, typed["unitPrice"]);

        // null means "cleared", and never "untouched" — an absent key is what says untouched.
        Assert.True(typed.ContainsKey("note"));
        Assert.Null(typed["note"]);
    }

    [Fact]
    public void NullIsNull_AndAnAlreadyTypedValueIsLeftAlone()
    {
        Assert.Null(AffidavitFieldValues.Typed(null, AffidavitFieldKind.Text));
        Assert.Equal(5, AffidavitFieldValues.Typed(5, AffidavitFieldKind.Number));
        Assert.Null(AffidavitFieldValues.Typed((IReadOnlyDictionary<string, object?>?)null));
    }

    private static AffidavitField Field(
        string name, object? value, string kind, object? previousValue = null) =>
        new(
            name,
            value,
            previousValue,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, name, 0.9f)),
            IsMandatory: false,
            Kind: kind);

    /// <summary>What every store does to an Affidavit, and what none of them used to undo.</summary>
    private static Affidavit RoundTrip(Affidavit affidavit) =>
        JsonSerializer.Deserialize<Affidavit>(JsonSerializer.Serialize(affidavit))!;

    private static object? Value(Affidavit affidavit, string name) =>
        affidavit.Fields.Single(f => f.Name == name).Value;
}
