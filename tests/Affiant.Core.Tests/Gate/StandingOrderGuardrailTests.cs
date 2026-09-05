namespace Affiant.Core.Tests.Gate;

using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Xunit;

/// <summary>
/// The three checks that hold a person-free approval back, in the order GT-5 fixes: the empty
/// required field, then PV-4's binding check, then the risk comparison (which lives with the
/// Standing Order base class that owns the ceiling — see <c>Affiant.Policies.Tests</c>).
///
/// <para>
/// These run over the <em>chain's</em> verdict, not only over the framework's Standing Order base
/// class, because the rule is that the gate checks before honouring a Standing Order — and a host
/// may implement <c>IApprovalPolicy</c> directly and return one without inheriting anything.
/// </para>
/// </summary>
public class StandingOrderGuardrailTests
{
    private static readonly ProvenanceSource[] External = [ProvenanceSource.External];

    // ── GT-5: the empty required field ───────────────────────────────────────────────────────

    [Fact]
    public void AMandatoryFieldReadingEmpty_DegradesTheVerdict_ToAPerson()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("title", "Q3 invoice"),
            TestAffidavits.Field("supplier", null, ProvenanceTag.Empty, isMandatory: true));

        var result = Apply(ReviewRequirement.StandingOrder, affidavit);

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result.Requirement);
        Assert.Equal(ReviewRequirement.StandingOrder, result.DegradedFrom);
        Assert.Equal(StandingOrderBlockedReasons.MandatoryFieldEmpty, result.BlockedReason);
        Assert.Contains("supplier", result.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The degrade changes who decides, not when the window closes: the policy's review window
    /// survives it intact (PV-4 and GT-5 both say so in the same words).
    /// </summary>
    [Fact]
    public void ADegradeKeepsTheVerdictsOwnReviewWindow()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("supplier", null, ProvenanceTag.Empty, isMandatory: true));

        var result = StandingOrderGuardrails.Apply(
            new ApprovalVerdict(ReviewRequirement.StandingOrder, TimeToLive: TimeSpan.FromMinutes(7)),
            affidavit, [], "test-policy");

        Assert.Equal(TimeSpan.FromMinutes(7), result.TimeToLive);
    }

    [Fact]
    public void AnOptionalFieldReadingEmpty_DoesNotHoldAStandingOrderBack()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("title", "Q3 invoice"),
            TestAffidavits.Field("note", null, ProvenanceTag.Empty));

        var result = Apply(ReviewRequirement.StandingOrder, affidavit);

        Assert.Equal(ReviewRequirement.StandingOrder, result.Requirement);
        Assert.Null(result.BlockedReason);
    }

    [Fact]
    public void AMandatoryFieldWithAKnownValue_DoesNotHoldAStandingOrderBack()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("supplier", "Acme", isMandatory: true));

        Assert.Equal(ReviewRequirement.StandingOrder, Apply(ReviewRequirement.StandingOrder, affidavit).Requirement);
    }

    // ── PV-4: the unbound declared input ─────────────────────────────────────────────────────

    [Fact]
    public void ADeclaredInputAboveConversation_WithNoBinding_DegradesTheVerdict()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("balance", 4200, Unbound(ProvenanceSource.External)));

        var result = Apply(ReviewRequirement.StandingOrder, affidavit, External);

        Assert.Equal(ReviewRequirement.ReviewerConfirmation, result.Requirement);
        Assert.Equal(StandingOrderBlockedReasons.UnboundDeclaredInput, result.BlockedReason);
        Assert.Contains("balance", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredInputThatPointsAtSomething_DoesNotDegradeTheVerdict()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("balance", 4200, Bound(ProvenanceSource.External)));

        Assert.Equal(
            ReviewRequirement.StandingOrder,
            Apply(ReviewRequirement.StandingOrder, affidavit, External).Requirement);
    }

    /// <summary>
    /// A policy that predicates on nothing outside the conversation is unaffected by PV-4, however
    /// unbound the tags on the record happen to be: the rule asks what the <em>verdict</em> rests
    /// on, and fixtures assert on the policy's declared inputs rather than on the Affidavit alone.
    /// </summary>
    [Fact]
    public void AnUnboundTagThePolicyDoesNotPredicateOn_IsNotPV4sBusiness()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("balance", 4200, Unbound(ProvenanceSource.External)));

        Assert.Equal(
            ReviewRequirement.StandingOrder,
            Apply(ReviewRequirement.StandingOrder, affidavit).Requirement);
    }

    [Fact]
    public void ADeclaredInputAtOrBelowConversation_NeedsNoBinding()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("title", "Q3 invoice", Unbound(ProvenanceSource.Conversation)));

        Assert.Equal(
            ReviewRequirement.StandingOrder,
            Apply(ReviewRequirement.StandingOrder, affidavit, [ProvenanceSource.Conversation]).Requirement);
    }

    // ── The order between them ───────────────────────────────────────────────────────────────

    /// <summary>
    /// More than one check can be true of the same proposal. The first to fire is the one the record
    /// names, and the verdict degrades exactly once — the empty required field before PV-4, because
    /// it depends on nothing the policy declared.
    /// </summary>
    [Fact]
    public void WhenBothChecksApply_TheRecordNamesTheEmptyRequiredField()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("supplier", null, ProvenanceTag.Empty, isMandatory: true),
            TestAffidavits.Field("balance", 4200, Unbound(ProvenanceSource.External)));

        var result = Apply(ReviewRequirement.StandingOrder, affidavit, External);

        Assert.Equal(StandingOrderBlockedReasons.MandatoryFieldEmpty, result.BlockedReason);
    }

    // ── The rules are about person-free approval only ────────────────────────────────────────

    [Theory]
    [InlineData(ReviewRequirement.ReviewerConfirmation)]
    [InlineData(ReviewRequirement.ReferralRequired)]
    [InlineData(ReviewRequirement.MultiParty)]
    public void ARequirementThatAlreadyAsksAPerson_IsLeftAlone(ReviewRequirement requirement)
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("supplier", null, ProvenanceTag.Empty, isMandatory: true));

        var result = Apply(requirement, affidavit, External);

        Assert.Equal(requirement, result.Requirement);
        Assert.Null(result.BlockedReason);
        Assert.Null(result.DegradedFrom);
    }

    // ── The predicates on their own ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyMandatoryFields_NamesEveryOne_InTheOrderTheAffidavitListsThem()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("supplier", null, ProvenanceTag.Empty, isMandatory: true),
            TestAffidavits.Field("title", "Q3 invoice"),
            TestAffidavits.Field("amount", null, ProvenanceTag.Empty, isMandatory: true));

        Assert.Equal(["supplier", "amount"], StandingOrderGuard.EmptyMandatoryFields(affidavit));
    }

    [Fact]
    public void FirstUnboundDeclaredInput_NamesTheFieldAndTheGrade()
    {
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("title", "Q3 invoice"),
            TestAffidavits.Field("balance", 4200, Unbound(ProvenanceSource.External)));

        var unbound = StandingOrderGuard.FirstUnboundDeclaredInput(affidavit, External);

        Assert.Equal("balance", unbound!.Field);
        Assert.Equal(ProvenanceSource.External, unbound.Source);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static ApprovalVerdict Apply(
        ReviewRequirement requirement,
        Affidavit affidavit,
        IReadOnlyCollection<ProvenanceSource>? declaredInputs = null) =>
        StandingOrderGuardrails.Apply(
            new ApprovalVerdict(requirement), affidavit, declaredInputs ?? [], "test-policy");

    private static ProvenanceTag Unbound(ProvenanceSource source) =>
        new(source, 0.9f, "asserted", null);

    private static ProvenanceTag Bound(ProvenanceSource source) =>
        new(source, 0.9f, "checked", null,
            new ProvenanceBinding.ExternalRef(new ExternalRecordRef("ledger", "txn-4711")));
}
