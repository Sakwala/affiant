namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// Verifies that ProvenanceChain preserves the full audit trail across multiple
/// merges without truncation (invariant R4).
/// </summary>
public class ProvenanceChainHistoryTests
{
    [Fact]
    public void Append_pushes_current_to_prior_and_sets_new_current()
    {
        var tag1 = new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, "turn 1", 1);
        var tag2 = new ProvenanceTag(ProvenanceSource.Inferred, 0.6f, "turn 2", 2);
        var tag3 = new ProvenanceTag(ProvenanceSource.Computed, 0.9f, "turn 3", 3);

        var chain1 = ProvenanceChain.From(tag1);
        Assert.Equal(ProvenanceSource.UserStated, chain1.Current.Source);
        Assert.Empty(chain1.Prior);

        var chain2 = chain1.Append(tag2);
        Assert.Equal(ProvenanceSource.Inferred, chain2.Current.Source);
        Assert.Single(chain2.Prior);
        Assert.Equal(ProvenanceSource.UserStated, chain2.Prior[0].Source);

        var chain3 = chain2.Append(tag3);
        Assert.Equal(ProvenanceSource.Computed, chain3.Current.Source);
        Assert.Equal(2, chain3.Prior.Count);
        Assert.Equal(ProvenanceSource.Inferred, chain3.Prior[0].Source);
        Assert.Equal(ProvenanceSource.UserStated, chain3.Prior[1].Source);
    }

    [Fact]
    public void Append_does_not_mutate_original_chain()
    {
        var tag1 = new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, null, null);
        var tag2 = new ProvenanceTag(ProvenanceSource.Inferred, 0.6f, null, null);

        var original = ProvenanceChain.From(tag1);
        _ = original.Append(tag2);

        // Original chain is unchanged
        Assert.Equal(ProvenanceSource.UserStated, original.Current.Source);
        Assert.Empty(original.Prior);
    }

    [Fact]
    public void AppendChain_concatenates_two_chains_preserving_order()
    {
        var tag1 = new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, "extractor1-turn1", 1);
        var tag2 = new ProvenanceTag(ProvenanceSource.Inferred, 0.6f, "extractor1-turn2", 2);
        var tag3 = new ProvenanceTag(ProvenanceSource.External, 0.9f, "extractor2-turn3", 3);

        // chain1: tag2 is current, tag1 is prior (tag1 older, tag2 newer)
        var chain1 = ProvenanceChain.From(tag1).Append(tag2);
        // chain2: tag3 is current (only tag)
        var chain2 = ProvenanceChain.From(tag3);

        var joined = chain1.AppendChain(chain2);

        // chain2's current becomes the new current; chain1's tags move into prior
        Assert.Equal(ProvenanceSource.External, joined.Current.Source);
        Assert.Equal(2, joined.Prior.Count);
        Assert.Equal(ProvenanceSource.Inferred, joined.Prior[0].Source);  // chain1's former current
        Assert.Equal(ProvenanceSource.UserStated, joined.Prior[1].Source); // chain1's prior
    }

    [Fact]
    public void AppendChain_preserves_all_tags_from_both_chains()
    {
        var tags = new[]
        {
            new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, "t1", 1),
            new ProvenanceTag(ProvenanceSource.External, 0.9f, "t2", 2),
            new ProvenanceTag(ProvenanceSource.Computed, 0.8f, "t3", 3),
        };

        var chainA = ProvenanceChain.From(tags[0]).Append(tags[1]);
        var chainB = ProvenanceChain.From(tags[2]);

        var joined = chainA.AppendChain(chainB);

        // Total tags: 1 current + 2 prior = 3
        Assert.Equal(ProvenanceSource.Computed, joined.Current.Source);
        Assert.Equal(2, joined.Prior.Count);
    }
}
