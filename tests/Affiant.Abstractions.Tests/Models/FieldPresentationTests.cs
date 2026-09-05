using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Xunit;

namespace Affiant.Abstractions.Tests.Models;

/// <summary>
/// A presentation hint says what constrains a reviewer's input on one field. A field the host
/// declared no constraint for gets no entry at all: absence is how the wire spells "render this
/// field from its own kind", and an entry that repeats the kind and says nothing else asks a
/// reviewer surface to treat an unconstrained field as a constrained one.
/// </summary>
public class FieldPresentationTests
{
    private static AffidavitField Field(string kind, IReadOnlyList<string>? allowed = null, string? pattern = null) =>
        new("f", "v", null, ProvenanceChain.From(ProvenanceTag.FromUser("f", null)),
            IsMandatory: false, Kind: kind, AllowedValues: allowed, Pattern: pattern);

    [Theory]
    [InlineData(AffidavitFieldKind.Text)]
    [InlineData(AffidavitFieldKind.Number)]
    [InlineData(AffidavitFieldKind.Date)]
    [InlineData(AffidavitFieldKind.Enum)]
    public void AFieldThatConstrainsNothing_GetsNoEntryAtAll(string kind)
    {
        Assert.Null(FieldPresentation.For(Field(kind)));
    }

    [Fact]
    public void AClosedSet_IsAHint()
    {
        var hint = FieldPresentation.For(Field(AffidavitFieldKind.Enum, allowed: ["Draft", "Active"]));

        Assert.NotNull(hint);
        Assert.Equal(AffidavitFieldKind.Enum, hint!.Kind);
        Assert.Equal(["Draft", "Active"], hint.AllowedValues);
    }

    [Fact]
    public void APattern_IsAHint()
    {
        var hint = FieldPresentation.For(Field(AffidavitFieldKind.Number, pattern: @"^\d+$"));

        Assert.NotNull(hint);
        Assert.Equal(@"^\d+$", hint!.Pattern);
    }
}
