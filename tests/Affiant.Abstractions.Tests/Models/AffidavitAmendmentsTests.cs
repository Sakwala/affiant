namespace Affiant.Abstractions.Tests.Models;

using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// What an accepted amendment does to the record, one test per checkable sentence: the three
/// numbers are recomputed; the amended field's current tag is a reviewer's act carrying a
/// <c>reviewer-act</c> binding that names the decision; that tag goes on top of the chain rather
/// than over it; and a cleared field follows the field-list rule instead, so clearing can never
/// raise a number.
/// </summary>
public class AffidavitAmendmentsTests
{
    private static readonly Guid EntryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset DecisionAt =
        new(2026, 9, 4, 10, 30, 0, TimeSpan.Zero);

    private static ProvenanceTag Machine(float confidence) =>
        new(ProvenanceSource.Inferred, confidence, "the model guessed", 3);

    private static Affidavit Proposal(params AffidavitField[] fields) =>
        Affidavit.Create("WriteCreate", "Widget", entityId: null, fields, warnings: []);

    private static AffidavitField Field(
        string name, object? value, float confidence, bool mandatory = false) =>
        new(name, value, null, ProvenanceChain.From(Machine(confidence)), mandatory);

    // ── The numbers are recomputed ───────────────────────────────────────────

    [Fact]
    public void AnAcceptedAmendment_RecomputesTheThreeNumbers()
    {
        var proposal = Proposal(
            Field("Colour", "read", 0.4f),
            Field("Weight", "1.5", 0.9f));

        var amended = AffidavitAmendments.Apply(
            proposal,
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        // The proposal reported the model's 0.4; the amended record reports the corrected minimum.
        Assert.Equal(0.4f, proposal.AggregateConfidence, 5);
        Assert.Equal(0.9f, amended.AggregateConfidence, 5);
        Assert.Equal(0.9f, amended.PopulatedConfidence!.Value, 5);
        Assert.Equal(0, amended.EmptyFieldCount);
    }

    [Fact]
    public void TheProposalItselfIsNotModified()
    {
        var proposal = Proposal(Field("Colour", "read", 0.4f));

        var amended = AffidavitAmendments.Apply(
            proposal,
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        Assert.Equal("read", proposal.Fields.Single().Value);
        Assert.Equal("red", amended.Fields.Single().Value);
    }

    // ── The reviewer's tag ───────────────────────────────────────────────────

    [Fact]
    public void AnAmendedFieldsCurrentTag_IsUserStated()
    {
        var amended = AffidavitAmendments.Apply(
            Proposal(Field("Colour", "read", 0.4f)),
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        Assert.Equal(ProvenanceSource.UserStated, amended.Fields.Single().Provenance.Current.Source);
    }

    [Fact]
    public void AnAmendedFieldsCurrentTag_CarriesAReviewerActBindingNamingTheDecision()
    {
        var amended = AffidavitAmendments.Apply(
            Proposal(Field("Colour", "read", 0.4f)),
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        var binding = Assert.IsType<ProvenanceBinding.ReviewerAct>(
            amended.Fields.Single().Provenance.Current.Binding);

        Assert.Equal(EntryId, binding.Ref.EntryId);
        Assert.Equal(DecisionAt, binding.Ref.DecisionAt);
    }

    [Fact]
    public void TheReviewersTag_GoesOnTopOfTheChain_NeverReplacingTheMachinesTag()
    {
        var machine = Machine(0.4f);
        var amended = AffidavitAmendments.Apply(
            Proposal(new AffidavitField("Colour", "read", null, ProvenanceChain.From(machine))),
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        var chain = amended.Fields.Single().Provenance;
        Assert.Equal(ProvenanceSource.UserStated, chain.Current.Source);
        Assert.Equal(machine, Assert.Single(chain.Prior));
    }

    [Fact]
    public void TheReviewersTag_WinsEvenWhenTheMachineWasMoreConfident()
    {
        // A merge would have left the machine's External/1.0 tag in force; a reviewer's act is not
        // a contest it can lose.
        var machine = new ProvenanceTag(ProvenanceSource.External, 1.0f, "the system of record", null);

        var amended = AffidavitAmendments.Apply(
            Proposal(new AffidavitField("Colour", "read", null, ProvenanceChain.From(machine))),
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        Assert.Equal(ProvenanceSource.UserStated, amended.Fields.Single().Provenance.Current.Source);
    }

    // ── What the map's three shapes mean ─────────────────────────────────────

    [Fact]
    public void AFieldTheMapDoesNotName_IsLeftUntouched()
    {
        var untouched = Field("Weight", "1.5", 0.9f);
        var amended = AffidavitAmendments.Apply(
            Proposal(Field("Colour", "read", 0.4f), untouched),
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        Assert.Equal(untouched, amended.Fields.Single(f => f.Name == "Weight"));
    }

    [Fact]
    public void AnAmendmentNamingAFieldTheAffidavitDoesNotPropose_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => AffidavitAmendments.Apply(
            Proposal(Field("Colour", "read", 0.4f)),
            new Dictionary<string, object?> { ["Notes"] = "hello" },
            EntryId, DecisionAt, "reviewer-1"));

        Assert.Contains("Notes", ex.Message);
    }

    [Fact]
    public void AnEmptyMap_ReturnsTheProposalUnchanged()
    {
        var proposal = Proposal(Field("Colour", "read", 0.4f));

        Assert.Same(proposal, AffidavitAmendments.Apply(
            proposal, new Dictionary<string, object?>(), EntryId, DecisionAt, "reviewer-1"));
        Assert.Same(proposal, AffidavitAmendments.Apply(
            proposal, null, EntryId, DecisionAt, "reviewer-1"));
    }

    // ── Clearing ─────────────────────────────────────────────────────────────

    [Fact]
    public void ClearingAMandatoryField_LeavesItPresentAndEmpty()
    {
        var amended = AffidavitAmendments.Apply(
            Proposal(Field("Colour", "read", 0.9f, mandatory: true)),
            new Dictionary<string, object?> { ["Colour"] = null },
            EntryId, DecisionAt, "reviewer-1");

        var field = Assert.Single(amended.Fields);
        Assert.Null(field.Value);
        Assert.Equal(ProvenanceSource.Empty, field.Provenance.Current.Source);
        Assert.Equal(0f, field.Provenance.Current.Confidence);
    }

    [Fact]
    public void ClearingAnOptionalField_RemovesItFromTheFieldList()
    {
        var amended = AffidavitAmendments.Apply(
            Proposal(Field("Colour", "read", 0.9f), Field("Weight", "1.5", 0.9f)),
            new Dictionary<string, object?> { ["Colour"] = null },
            EntryId, DecisionAt, "reviewer-1");

        Assert.Equal("Weight", Assert.Single(amended.Fields).Name);
    }

    [Fact]
    public void ClearingCanNeverRaiseANumber()
    {
        var proposal = Proposal(
            Field("Colour", "read", 0.9f, mandatory: true),
            Field("Weight", "1.5", 0.9f));

        var amended = AffidavitAmendments.Apply(
            proposal,
            new Dictionary<string, object?> { ["Colour"] = null },
            EntryId, DecisionAt, "reviewer-1");

        // The reviewer's own maximal tag is NOT written over the emptied field: doing so would make
        // a record that reported perfect confidence over nothing once every field was wiped.
        Assert.Equal(0.9f, proposal.AggregateConfidence, 5);
        Assert.Equal(0f, amended.AggregateConfidence, 5);
        Assert.Equal(1, amended.EmptyFieldCount);
    }

    [Fact]
    public void AClearedFieldStillCarriesTheReviewersAct()
    {
        var amended = AffidavitAmendments.Apply(
            Proposal(Field("Colour", "read", 0.9f, mandatory: true)),
            new Dictionary<string, object?> { ["Colour"] = null },
            EntryId, DecisionAt, "reviewer-1");

        var binding = Assert.IsType<ProvenanceBinding.ReviewerAct>(
            amended.Fields.Single().Provenance.Current.Binding);

        Assert.Equal(EntryId, binding.Ref.EntryId);
    }

    [Fact]
    public void AnAmendmentNeverMovesAFieldsPreviousValue()
    {
        var proposal = Affidavit.Create(
            "WriteUpdate", "Widget", "widget-1",
            [new AffidavitField("Colour", "read", "blue", ProvenanceChain.From(Machine(0.4f)))],
            warnings: []);

        var amended = AffidavitAmendments.Apply(
            proposal,
            new Dictionary<string, object?> { ["Colour"] = "red" },
            EntryId, DecisionAt, "reviewer-1");

        // previousValue is what the entity holds now, which an amendment does not change.
        Assert.Equal("blue", amended.Fields.Single().PreviousValue);
    }
}
