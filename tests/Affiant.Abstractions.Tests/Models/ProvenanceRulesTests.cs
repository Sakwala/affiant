namespace Affiant.Abstractions.Tests.Models;

using System.Text.Json;
using System.Text.Json.Serialization;
using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// The provenance rules, one test per checkable sentence: the seven-source ladder, the chain, the
/// merge, the confidence range, the binding kinds, and the structural restriction on what an
/// implementation's own inference may mint.
/// </summary>
public class ProvenanceRulesTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // ── The ladder ───────────────────────────────────────────────────────────

    [Fact]
    public void TheLadder_IsSevenSources_MostDeterministicFirst()
    {
        Assert.Equal(
            [
                ProvenanceSource.UserStated,
                ProvenanceSource.External,
                ProvenanceSource.Computed,
                ProvenanceSource.Conversation,
                ProvenanceSource.Inferred,
                ProvenanceSource.Default,
                ProvenanceSource.Empty,
            ],
            Enum.GetValues<ProvenanceSource>());
    }

    // ── Confidence range ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.4f, 1.0f)]
    [InlineData(42f, 1.0f)]
    public void Confidence_IsCappedAtOne(float reported, float expected)
    {
        Assert.Equal(expected, new ProvenanceTag(ProvenanceSource.Inferred, reported, null, null).Confidence);
    }

    [Theory]
    [InlineData(-0.2f)]
    [InlineData(float.NegativeInfinity)]
    public void Confidence_IsFlooredAtZero(float reported)
    {
        Assert.Equal(0f, new ProvenanceTag(ProvenanceSource.Inferred, reported, null, null).Confidence);
    }

    [Fact]
    public void Confidence_NaN_IsNotAClaim_AndReadsZero()
    {
        Assert.Equal(0f, new ProvenanceTag(ProvenanceSource.Inferred, float.NaN, null, null).Confidence);
    }

    [Fact]
    public void Confidence_IsClampedOnEveryMintSite()
    {
        Assert.Equal(1.0f, ProvenanceTag.FromTool("Lookup", 3f).Confidence);
        Assert.Equal(1.0f, ProvenanceTag.FromInference(InferenceSource.Inferred, "Colour", 3f).Confidence);
        Assert.Equal(0f, ProvenanceTag.FromDefault("fallback", -3f).Confidence);
    }

    [Fact]
    public void Confidence_IsClampedOnACopyToo()
    {
        var tag = new ProvenanceTag(ProvenanceSource.Inferred, 0.6f, null, null);

        // `with` goes through the init accessor, so a copy cannot smuggle a value past the clamp.
        Assert.Equal(1.0f, (tag with { Confidence = 5f }).Confidence);
        Assert.Equal(0f, (tag with { Confidence = -5f }).Confidence);
        // And a copy that changes the source to Empty reads 0, whatever it was carrying.
        Assert.Equal(0f, (tag with { Source = ProvenanceSource.Empty }).Confidence);
    }

    [Fact]
    public void AnEmptyTag_AlwaysCarriesZero()
    {
        Assert.Equal(0f, ProvenanceTag.Empty.Confidence);
        // Even when a caller claims otherwise: "nobody knows where this came from" cannot also be
        // a confident claim.
        Assert.Equal(0f, new ProvenanceTag(ProvenanceSource.Empty, 0.9f, null, null).Confidence);
    }

    // ── The chain and the merge ──────────────────────────────────────────────

    [Fact]
    public void AChain_IsTheOrderedHistoryWithACurrentTag()
    {
        var first = new ProvenanceTag(ProvenanceSource.Default, 0.3f, "default", null);
        var second = new ProvenanceTag(ProvenanceSource.Inferred, 0.6f, "inferred", null);

        var chain = ProvenanceChain.From(first).Merge(second);

        Assert.Equal(second, chain.Current);
        Assert.Equal([first], chain.Prior);
    }

    [Fact]
    public void Merge_TheHigherConfidenceWins()
    {
        var incumbent = new ProvenanceTag(ProvenanceSource.Inferred, 0.4f, null, null);
        var challenger = new ProvenanceTag(ProvenanceSource.Inferred, 0.9f, null, null);

        Assert.Equal(challenger, ProvenanceChain.From(incumbent).Merge(challenger).Current);
        Assert.Equal(challenger, ProvenanceChain.From(challenger).Merge(incumbent).Current);
    }

    [Fact]
    public void Merge_TiesBreakTowardTheMoreDeterministicSource()
    {
        var lessDeterministic = new ProvenanceTag(ProvenanceSource.Inferred, 0.7f, null, null);
        var moreDeterministic = new ProvenanceTag(ProvenanceSource.External, 0.7f, null, null);

        Assert.Equal(
            moreDeterministic,
            ProvenanceChain.From(lessDeterministic).Merge(moreDeterministic).Current);
    }

    [Fact]
    public void Merge_AnExactTieLeavesTheIncumbentInForce()
    {
        var incumbent = new ProvenanceTag(ProvenanceSource.Inferred, 0.7f, "first", null);
        var challenger = new ProvenanceTag(ProvenanceSource.Inferred, 0.7f, "second", null);

        var chain = ProvenanceChain.From(incumbent).Merge(challenger);

        Assert.Equal(incumbent, chain.Current);
        Assert.Equal([challenger], chain.Prior);
    }

    [Fact]
    public void Merge_TheLosingTagIsPreservedInTheChain()
    {
        var loser = new ProvenanceTag(ProvenanceSource.Inferred, 0.4f, "the model's guess", null);
        var winner = new ProvenanceTag(ProvenanceSource.External, 0.95f, "the system of record", null);

        var chain = ProvenanceChain.From(loser).Merge(winner);

        Assert.Equal(winner, chain.Current);
        Assert.Contains(loser, chain.Prior);
    }

    [Fact]
    public void Merge_TheLosingChallengerIsPreservedToo()
    {
        var incumbent = new ProvenanceTag(ProvenanceSource.External, 0.95f, null, null);
        var challenger = new ProvenanceTag(ProvenanceSource.Inferred, 0.4f, "the model disagreed", null);

        var chain = ProvenanceChain.From(incumbent).Merge(challenger);

        Assert.Equal(incumbent, chain.Current);
        Assert.Contains(challenger, chain.Prior);
    }

    [Fact]
    public void AReviewersAct_IsNotAConfidenceContest_AndSupersedesOutright()
    {
        // The field was already stated by a person once — prefilled from a form. A reviewer then
        // corrects it. Both tags are UserStated at 1.0, so the merge rule's exact-tie clause would
        // leave the ORIGINAL in force and file the correction away as history: the reviewer's
        // decision silently discarded. Appending is what the amendment path does instead.
        var prefill = ProvenanceTag.FromUser(
            "Colour", new ProvenanceBinding.FormInput(new FormInputRef("colour")));
        var correction = ProvenanceTag.FromUser(
            "Colour",
            new ProvenanceBinding.ReviewerAct(
                new ReviewerActRef(Guid.NewGuid(), DateTimeOffset.UnixEpoch)));

        Assert.Equal(prefill, ProvenanceChain.From(prefill).Merge(correction).Current);

        var chain = ProvenanceChain.From(prefill).Append(correction);
        Assert.Equal(correction, chain.Current);
        Assert.Contains(prefill, chain.Prior);
    }

    // ── Bindings ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheBindingKinds_AreAFixedSetOfFive()
    {
        Assert.Equal(
            ["utterance-span", "reviewer-act", "form-input", "external-ref", "computation-ref"],
            ProvenanceBindingKind.All);
    }

    [Fact]
    public void EachBindingKind_ReportsItsOwnDiscriminator()
    {
        Assert.Equal(
            ProvenanceBindingKind.UtteranceSpan,
            new ProvenanceBinding.UtteranceSpan(new UtteranceSpanRef(0, 4, "sha")).Kind);
        Assert.Equal(
            ProvenanceBindingKind.ReviewerAct,
            new ProvenanceBinding.ReviewerAct(new ReviewerActRef(Guid.Empty, DateTimeOffset.UnixEpoch)).Kind);
        Assert.Equal(
            ProvenanceBindingKind.FormInput,
            new ProvenanceBinding.FormInput(new FormInputRef("colour")).Kind);
        Assert.Equal(
            ProvenanceBindingKind.ExternalRef,
            new ProvenanceBinding.ExternalRef(new ExternalRecordRef("ledger", "4711")).Kind);
        Assert.Equal(
            ProvenanceBindingKind.ComputationRef,
            new ProvenanceBinding.ComputationRef(new ComputationRuleRef("vat", ["net"])).Kind);
    }

    [Fact]
    public void ABinding_TravelsAsKindAndRef()
    {
        var tag = new ProvenanceTag(
            ProvenanceSource.External,
            0.95f,
            "from the ledger",
            null,
            new ProvenanceBinding.ExternalRef(new ExternalRecordRef("ledger", "4711")));

        var json = JsonSerializer.SerializeToElement(tag, WebJson);
        var binding = json.GetProperty("binding");

        Assert.Equal("external-ref", binding.GetProperty("kind").GetString());
        Assert.Equal("ledger", binding.GetProperty("ref").GetProperty("system").GetString());
        Assert.Equal("4711", binding.GetProperty("ref").GetProperty("recordId").GetString());
    }

    [Theory]
    [InlineData(ProvenanceBindingKind.UtteranceSpan)]
    [InlineData(ProvenanceBindingKind.ReviewerAct)]
    [InlineData(ProvenanceBindingKind.FormInput)]
    [InlineData(ProvenanceBindingKind.ExternalRef)]
    [InlineData(ProvenanceBindingKind.ComputationRef)]
    public void EveryBindingKind_RoundTripsThroughJson(string kind)
    {
        ProvenanceBinding binding = kind switch
        {
            ProvenanceBindingKind.UtteranceSpan =>
                new ProvenanceBinding.UtteranceSpan(new UtteranceSpanRef(12, 5, "sha256:abc")),
            ProvenanceBindingKind.ReviewerAct =>
                new ProvenanceBinding.ReviewerAct(
                    new ReviewerActRef(Guid.Parse("11111111-1111-1111-1111-111111111111"), DateTimeOffset.UnixEpoch)),
            ProvenanceBindingKind.FormInput =>
                new ProvenanceBinding.FormInput(new FormInputRef("colour")),
            ProvenanceBindingKind.ExternalRef =>
                new ProvenanceBinding.ExternalRef(new ExternalRecordRef(
                    "ledger", "4711", DateTimeOffset.UnixEpoch, "sha256:def",
                    new RelayRef("relay-1", "+94...", "msg-9"))),
            _ => new ProvenanceBinding.ComputationRef(new ComputationRuleRef(
                "vat", ["net", "rate"], new ComputationConstantRef("hmrc.gov.uk", "2026-03-01"))),
        };

        var tag = new ProvenanceTag(ProvenanceSource.External, 0.9f, null, null, binding);
        var json = JsonSerializer.Serialize(tag, WebJson);
        var round = JsonSerializer.Deserialize<ProvenanceTag>(json, WebJson);

        Assert.NotNull(round);
        Assert.Equal(kind, round.Binding!.Kind);
        // Compared as bytes rather than as records: a binding whose ref carries a list would
        // compare by reference, and what has to survive the trip is the payload.
        Assert.Equal(json, JsonSerializer.Serialize(round, WebJson));
    }

    [Fact]
    public void ATagWithNoBinding_IsRecordedAsUnbound()
    {
        var tag = new ProvenanceTag(ProvenanceSource.External, 0.9f, null, null);

        Assert.Null(tag.Binding);
        Assert.False(tag.IsBound);
    }

    [Theory]
    [InlineData(ProvenanceSource.UserStated, true)]
    [InlineData(ProvenanceSource.External, true)]
    [InlineData(ProvenanceSource.Computed, true)]
    [InlineData(ProvenanceSource.Conversation, false)]
    [InlineData(ProvenanceSource.Inferred, false)]
    [InlineData(ProvenanceSource.Default, false)]
    [InlineData(ProvenanceSource.Empty, false)]
    public void OnlyTheThreeSourcesAboveConversation_NeedABinding(ProvenanceSource source, bool expected)
    {
        Assert.Equal(expected, ProvenanceTag.RequiresBinding(source));
    }

    // ── What an inference may mint ───────────────────────────────────────────

    [Fact]
    public void Inference_MintsConversation_WhenTheValueWasLiterallyInTheTurn()
    {
        var tag = ProvenanceTag.FromInference(InferenceSource.Conversation, "Colour", 0.8f);
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
    }

    [Fact]
    public void Inference_MintsInferred_WhenTheModelReasonedToTheValue()
    {
        var tag = ProvenanceTag.FromInference(InferenceSource.Inferred, "Colour", 0.8f);
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
    }

    [Fact]
    public void Inference_HasNoWayToNameAnyOtherSource()
    {
        // Structural, not a convention: the inference factory's source parameter is InferenceSource,
        // which has exactly two members — so "UserStated", "External" and "Computed" are not values
        // that can be passed, and the restriction cannot be violated by a caller at all.
        Assert.Equal(
            [InferenceSource.Conversation, InferenceSource.Inferred],
            Enum.GetValues<InferenceSource>());

        var parameter = typeof(ProvenanceTag)
            .GetMethod(nameof(ProvenanceTag.FromInference))!
            .GetParameters()[0];

        Assert.Equal(typeof(InferenceSource), parameter.ParameterType);
    }

    [Fact]
    public void OnlyAPersonsAct_MintsUserStated()
    {
        var tag = ProvenanceTag.FromUser("Colour", new ProvenanceBinding.FormInput(new FormInputRef("colour")));

        Assert.Equal(ProvenanceSource.UserStated, tag.Source);
        Assert.Equal(1.0f, tag.Confidence);
        Assert.True(tag.IsBound);
    }
}
