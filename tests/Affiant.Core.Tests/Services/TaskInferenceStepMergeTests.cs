namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Xunit;

/// <summary>
/// Verifies TaskInferenceStep's confidence-based merge rule (invariant R3).
/// Higher confidence wins; ties break by ProvenanceSource ordinal (lower = more deterministic).
/// ProvenanceSource ordinals: UserStated=0, External=1, Computed=2, Conversation=3, Inferred=4.
/// </summary>
public class TaskInferenceStepMergeTests
{
    [Fact]
    public void Higher_confidence_wins_in_merge()
    {
        var lowConfidence = new ProvenanceTag(
            Source: ProvenanceSource.Inferred,
            Confidence: 0.4f,
            Evidence: "LLM inferred",
            ConversationTurn: null);

        var highConfidence = new ProvenanceTag(
            Source: ProvenanceSource.UserStated,
            Confidence: 1.0f,
            Evidence: "User stated",
            ConversationTurn: 2);

        var winner = TaskInferenceStep.ResolveByConfidence(lowConfidence, highConfidence);

        Assert.Equal(ProvenanceSource.UserStated, winner.Source);
        Assert.Equal(1.0f, winner.Confidence);
    }

    [Fact]
    public void Lower_confidence_does_not_displace_higher()
    {
        var highConfidence = new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, null, null);
        var lowConfidence = new ProvenanceTag(ProvenanceSource.Inferred, 0.3f, null, null);

        var winner = TaskInferenceStep.ResolveByConfidence(highConfidence, lowConfidence);

        Assert.Equal(ProvenanceSource.UserStated, winner.Source);
    }

    // Tie-breaking: equal confidence, lower Source ordinal wins.
    // Ordinals: UserStated=0, External=1, Computed=2, Inferred=4.
    [Theory]
    [InlineData(ProvenanceSource.UserStated, ProvenanceSource.External, ProvenanceSource.UserStated)]
    [InlineData(ProvenanceSource.External, ProvenanceSource.Computed, ProvenanceSource.External)]
    [InlineData(ProvenanceSource.Computed, ProvenanceSource.Inferred, ProvenanceSource.Computed)]
    public void Tie_breaks_to_more_deterministic_source(
        ProvenanceSource sourceA,
        ProvenanceSource sourceB,
        ProvenanceSource expectedWinner)
    {
        var tagA = new ProvenanceTag(sourceA, 0.8f, null, null);
        var tagB = new ProvenanceTag(sourceB, 0.8f, null, null);

        var winner = TaskInferenceStep.ResolveByConfidence(tagA, tagB);

        Assert.Equal(expectedWinner, winner.Source);
    }
}
