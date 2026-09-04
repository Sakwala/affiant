namespace Affiant.Core.Tests.Gate;

using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The gate runs in the order the protocol fixes (GT-1): <b>substance refusal (GT-3) → the policy
/// chain → the deadline stamped from what the chain returned (GT-4) → filed</b>. Each rule sentence
/// gets its own test, and the order itself is asserted from a transcript rather than inferred from
/// an outcome that several orders could produce.
/// </summary>
public class ReviewGatePipelineOrderTests
{
    // ── GT-1: the order ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePolicyChainRuns_BeforeTheRowIsFiled()
    {
        var transcript = new List<string>();
        var store = new RecordingDocketStore();
        var gate = BuildGate(
            store,
            new ScriptedPolicy(ReviewRequirement.ReviewerConfirmation, () => transcript.Add("policy")));
        store.Filed.Clear();

        await gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive()));

        // The policy spoke first; the row was filed after it, exactly once.
        Assert.Equal("policy", transcript[0]);
        Assert.Single(store.Filed);
        Assert.Equal(["get", "file"], store.Calls);
    }

    [Fact]
    public async Task ASubstanceRefusal_HappensBeforeThePolicyChainIsAsked()
    {
        var asked = false;
        var store = new RecordingDocketStore();
        var gate = BuildGate(store, new ScriptedPolicy(ReviewRequirement.StandingOrder, () => asked = true));

        await Assert.ThrowsAsync<AffiantSubstanceException>(
            () => gate.FileForReviewAsync(Proposal(), Context(Hollow())));

        Assert.False(asked, "GT-3: no Standing Order ever sees a hollow proposal.");
    }

    [Fact]
    public async Task ASubstanceRefusal_TouchesTheStoreNotAtAll()
    {
        var store = new RecordingDocketStore();
        var transport = new RecordingTransport();
        var gate = BuildGate(store, transport: transport);

        await Assert.ThrowsAsync<AffiantSubstanceException>(
            () => gate.FileForReviewAsync(Proposal(), Context(Hollow())));

        Assert.Empty(store.Calls);
        Assert.Empty(store.Filed);
        Assert.Empty(transport.Broadcasts);
    }

    // ── GT-3: what counts as swearing to nothing ─────────────────────────────────────────────

    [Fact]
    public async Task AProposalWithNoFieldsAtAll_IsRefused()
    {
        var gate = BuildGate(new RecordingDocketStore());

        var refusal = await Assert.ThrowsAsync<AffiantSubstanceException>(
            () => gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Of())));

        Assert.Equal(ToolErrorCodes.SubstanceRefused, refusal.Code);
    }

    [Fact]
    public async Task AValueAssertedUnderEmptyProvenance_IsTheHollowSignature_AndIsRefused()
    {
        var gate = BuildGate(new RecordingDocketStore());
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("title", "Q3 invoice"),
            TestAffidavits.Field("amount", 4200, ProvenanceTag.Empty));

        var refusal = await Assert.ThrowsAsync<AffiantSubstanceException>(
            () => gate.FileForReviewAsync(Proposal(), Context(affidavit)));

        Assert.Contains("amount", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryFieldTaggedEmpty_IsRefused_EvenWithNoValuesAsserted()
    {
        var gate = BuildGate(new RecordingDocketStore());
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("title", null, ProvenanceTag.Empty),
            TestAffidavits.Field("amount", "  ", ProvenanceTag.Empty));

        await Assert.ThrowsAsync<AffiantSubstanceException>(
            () => gate.FileForReviewAsync(Proposal(), Context(affidavit)));
    }

    /// <summary>
    /// <c>0</c>, <see langword="false"/> and an empty collection are values a field can honestly
    /// swear to. A proposal that says "the count is zero" is a proposal, not a hollow one — the
    /// hollow signature is a value with no provenance, and an unknown field with no value is honest.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(false)]
    public async Task ZeroAndFalseAreValues_NotEmptiness(object value)
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(store);
        var affidavit = TestAffidavits.Of(
            TestAffidavits.Field("count", value),
            TestAffidavits.Field("note", null, ProvenanceTag.Empty));

        await gate.FileForReviewAsync(Proposal(), Context(affidavit));

        Assert.Single(store.Filed);
    }

    [Fact]
    public async Task AnEmptyCollectionIsAValue_NotEmptiness()
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(store);

        await gate.FileForReviewAsync(
            Proposal(), Context(TestAffidavits.Of(TestAffidavits.Field("tags", Array.Empty<string>()))));

        Assert.Single(store.Filed);
    }

    // ── GT-4: the deadline, from the policy result, after the chain ──────────────────────────

    [Fact]
    public async Task TheDeadlineComesFromTheVerdict_WhenTheVerdictNamesOne()
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(
            store,
            new ScriptedPolicy(new ApprovalVerdict(
                ReviewRequirement.ReviewerConfirmation, TimeToLive: TimeSpan.FromMinutes(5))),
            gateDefault: TimeSpan.FromMinutes(30));

        await gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive()));

        AssertWindowIsAbout(TimeSpan.FromMinutes(5), store.Filed[0]);
    }

    [Fact]
    public async Task TheDeadlineComesFromThePolicysOwnDefault_WhenTheVerdictNamesNone()
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(
            store,
            new ScriptedPolicy(
                ReviewRequirement.ReviewerConfirmation,
                defaultTimeToLive: TimeSpan.FromMinutes(15)),
            gateDefault: TimeSpan.FromMinutes(30));

        await gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive()));

        AssertWindowIsAbout(TimeSpan.FromMinutes(15), store.Filed[0]);
    }

    [Fact]
    public async Task TheDeadlineFallsBackToTheGatesDefault_WhenNeitherNamesOne()
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(
            store,
            new ScriptedPolicy(ReviewRequirement.ReviewerConfirmation),
            gateDefault: TimeSpan.FromMinutes(30));

        await gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive()));

        AssertWindowIsAbout(TimeSpan.FromMinutes(30), store.Filed[0]);
    }

    /// <summary>
    /// A verdict's window beats the policy's own default, which beats the gate's — three sources,
    /// one order, asserted together so a change to any one of them cannot pass by moving another.
    /// </summary>
    [Fact]
    public async Task TheVerdictsWindowBeatsThePolicysDefault()
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(
            store,
            new ScriptedPolicy(
                new ApprovalVerdict(ReviewRequirement.ReviewerConfirmation, TimeToLive: TimeSpan.FromMinutes(5)),
                defaultTimeToLive: TimeSpan.FromMinutes(15)),
            gateDefault: TimeSpan.FromMinutes(30));

        await gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive()));

        AssertWindowIsAbout(TimeSpan.FromMinutes(5), store.Filed[0]);
    }

    [Fact]
    public async Task AReFileWithTheSameId_KeepsTheExistingDeadline_AndFilesNoSecondEntry()
    {
        var store = new RecordingDocketStore();
        var transport = new RecordingTransport();
        var gate = BuildGate(
            store,
            new ScriptedPolicy(ReviewRequirement.ReviewerConfirmation),
            transport: transport,
            gateDefault: TimeSpan.FromMinutes(30));

        var entryId = Guid.NewGuid();
        var context = Context(TestAffidavits.Substantive()) with { EntryId = entryId };

        await gate.FileForReviewAsync(Proposal(), context);
        var firstDeadline = store.Filed.Single().ExpiresAt;

        var replay = await gate.FileForReviewAsync(Proposal(), context);

        Assert.IsType<ReviewFilingResult.RequiresReview>(replay);
        Assert.Single(store.Filed);
        Assert.Equal(firstDeadline, store.Filed.Single().ExpiresAt);

        // The replay re-broadcasts the card, and the card carries the deadline the ROW holds — a
        // reviewer shown a fresh deadline the record does not hold is being shown a lie.
        Assert.Equal(2, transport.Cards.Count);
        Assert.Equal(firstDeadline, transport.Cards[1].RequiredBy);
    }

    // ── CV-1: the two policy faults, refused at evaluation with nothing filed ────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AVerdictNamingAWindowThatIsNotADeadline_IsRefused_AndNothingIsFiled(int minutes)
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(store, new ScriptedPolicy(new ApprovalVerdict(
            ReviewRequirement.ReviewerConfirmation, TimeToLive: TimeSpan.FromMinutes(minutes))));

        var refusal = await Assert.ThrowsAsync<AffiantPolicyException>(
            () => gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive())));

        Assert.Equal(ToolErrorCodes.WireUpInvalid, refusal.Code);
        Assert.Empty(store.Filed);
    }

    [Fact]
    public async Task APolicysOwnDefaultThatIsNotADeadline_IsRefused_AndNothingIsFiled()
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(store, new ScriptedPolicy(
            ReviewRequirement.ReviewerConfirmation, defaultTimeToLive: TimeSpan.Zero));

        await Assert.ThrowsAsync<AffiantPolicyException>(
            () => gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive())));

        Assert.Empty(store.Filed);
    }

    [Fact]
    public async Task APolicyThatThrows_IsRefused_AndNothingIsFiled()
    {
        var store = new RecordingDocketStore();
        var gate = BuildGate(store, new ThrowingPolicy());

        var refusal = await Assert.ThrowsAsync<AffiantPolicyException>(
            () => gate.FileForReviewAsync(Proposal(), Context(TestAffidavits.Substantive())));

        Assert.Equal(ToolErrorCodes.WireUpInvalid, refusal.Code);
        Assert.IsType<InvalidOperationException>(refusal.InnerException);
        Assert.Empty(store.Filed);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static WriteProposal Proposal() =>
        new("CreateOrder", DateTimeOffset.UtcNow, TestAffidavits.Substantive());

    private static ReviewContext Context(Affidavit affidavit) => new(
        SessionId: "session-1",
        TenantId: "tenant-1",
        UserId: "user-1",
        ReviewerUserId: "reviewer-1",
        Affidavit: affidavit);

    private static Affidavit Hollow() =>
        TestAffidavits.Of(TestAffidavits.Field("amount", 4200, ProvenanceTag.Empty));

    private static ReviewGate BuildGate(
        RecordingDocketStore store,
        IApprovalPolicy? policy = null,
        IStreamingTransport? transport = null,
        TimeSpan? gateDefault = null)
    {
        var evaluator = new ApprovalPolicyEvaluator(policy is null ? [] : [policy]);
        var options = new AffiantCoreOptions
        {
            EnableObservability = false,
            DefaultDocketTtl = gateDefault ?? TimeSpan.FromMinutes(30),
        };
        return new ReviewGate(
            transport ?? new RecordingTransport(), store, evaluator, options,
            NullLogger<ReviewGate>.Instance);
    }

    /// <summary>
    /// The window between filing and the deadline, to the minute. Exact equality would test the
    /// clock, not the rule; a clock seam is a separate change.
    /// </summary>
    private static void AssertWindowIsAbout(TimeSpan expected, DocketEntry entry)
    {
        var actual = entry.ExpiresAt - entry.CreatedAt;
        Assert.True(
            (actual - expected).Duration() < TimeSpan.FromSeconds(30),
            $"expected a review window of about {expected}, got {actual}");
    }
}
