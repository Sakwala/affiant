namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Xunit;

/// <summary>
/// GT-4: a Docket entry id is derived from the proposal, and the derivation is the protocol's.
/// </summary>
/// <remarks>
/// <para>
/// The three ids below are not this implementation's own output: they were produced by the
/// protocol's reference implementation for exactly this material, and are pinned here as vectors.
/// The id is inside the record — an accepted amendment's <c>reviewer-act</c> binding names it — so
/// it is inside the content hash an execution grant binds to. Two implementations that derived
/// different ids for the same proposal would disagree about which row a proposal IS, and a grant
/// minted by one would not validate against the other.
/// </para>
/// </remarks>
public class EntryIdDerivationTests
{
    private static Affidavit Affidavit(string operationType, string entityType, string? entityId, params string[] fields) =>
        new(
            operationType,
            entityType,
            entityId,
            [.. fields.Select(f => new AffidavitField(f, null, null, ProvenanceChain.From(ProvenanceTag.Empty)))],
            AggregateConfidence: 0f,
            PopulatedConfidence: null,
            EmptyFieldCount: fields.Length,
            Warnings: [],
            RequiresConfirmation: true);

    [Fact]
    public void AnUpdateWithNoArguments_DerivesTheReferenceId()
    {
        var id = EntryIdDerivation.Derive(
            "tenant-a",
            "conv-1",
            "update_invoice",
            Affidavit("WriteUpdate", "Invoice", "invoice-1", "status", "amount", "note"),
            arguments: null,
            supersedes: null);

        Assert.Equal(Guid.Parse("4f3f031b-a7c4-867c-9b1f-be0de416040d"), id);
    }

    [Fact]
    public void ACreateWithArguments_DerivesTheReferenceId()
    {
        var id = EntryIdDerivation.Derive(
            "tenant-b",
            "conv-2",
            "create_widget",
            Affidavit("WriteCreate", "Widget", null, "name", "size"),
            new Dictionary<string, object?> { ["name"] = "gizmo", ["size"] = 4 },
            supersedes: null);

        Assert.Equal(Guid.Parse("b70a9b20-9907-89c8-97df-807adb0500f3"), id);
    }

    [Fact]
    public void AResubmission_NamesTheRowItReplaces_AndDerivesTheReferenceId()
    {
        var id = EntryIdDerivation.Derive(
            "tenant-a",
            "conv-1",
            "update_invoice",
            Affidavit("WriteUpdate", "Invoice", "invoice-1", "status"),
            new Dictionary<string, object?> { ["status"] = "Active" },
            supersedes: Guid.Parse("8f14e45f-ceea-467e-bd76-000000000001"));

        Assert.Equal(Guid.Parse("af734c99-d3f5-8727-9f51-d3dbfa962bf8"), id);
    }

    /// <summary>
    /// The version and variant nibbles are the layout's, not the digest's: a derived id is a
    /// name-based UUID and says so.
    /// </summary>
    [Fact]
    public void TheIdIsAVersion8Uuid()
    {
        var id = EntryIdDerivation.Derive(
            "tenant-a", "conv-1", "update_invoice",
            Affidavit("WriteUpdate", "Invoice", "invoice-1", "status"),
            arguments: null,
            supersedes: null);

        var text = id.ToString();
        Assert.Equal('8', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    /// <summary>
    /// The arguments are part of the material: two calls that differ only in what the model passed
    /// are two proposals, and a retry of the same call is a replay of the same row (GT-4).
    /// </summary>
    [Fact]
    public void TheArgumentsArePartOfTheIdentity()
    {
        var affidavit = Affidavit("WriteUpdate", "Invoice", "invoice-1", "status");

        var active = EntryIdDerivation.Derive(
            "tenant-a", "conv-1", "update_invoice", affidavit,
            new Dictionary<string, object?> { ["status"] = "Active" }, supersedes: null);
        var retired = EntryIdDerivation.Derive(
            "tenant-a", "conv-1", "update_invoice", affidavit,
            new Dictionary<string, object?> { ["status"] = "Retired" }, supersedes: null);
        var again = EntryIdDerivation.Derive(
            "tenant-a", "conv-1", "update_invoice", affidavit,
            new Dictionary<string, object?> { ["status"] = "Active" }, supersedes: null);

        Assert.NotEqual(active, retired);
        Assert.Equal(active, again);
    }
}
