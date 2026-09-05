namespace Affiant.Abstractions.Models;

/// <summary>
/// Ordered history of provenance tags for a single field. The framework's audit
/// trail — answers "how did this field arrive at its current value?" by walking
/// the chain from <see cref="Current"/> through <see cref="Prior"/> (newest first).
///
/// <para>
/// Nothing is ever dropped from a chain. A merge that discarded the loser would erase the fact that
/// two producers disagreed, which is the fact a reviewer most wants to see.
/// </para>
///
/// Matches framework specification §2.3.
/// </summary>
public sealed record ProvenanceChain(
    ProvenanceTag Current,
    IReadOnlyList<ProvenanceTag> Prior)
{
    /// <summary>
    /// Start a new chain from a single tag, with an empty <see cref="Prior"/> list.
    /// </summary>
    public static ProvenanceChain From(ProvenanceTag tag) =>
        new(tag, Array.Empty<ProvenanceTag>());

    /// <summary>
    /// Put <paramref name="newer"/> in force unconditionally — it becomes <see cref="Current"/> and
    /// the previous <see cref="Current"/> is prepended to <see cref="Prior"/> (preserving
    /// newest-first ordering).
    ///
    /// <para>
    /// <b>Not a merge, deliberately.</b> A reviewer's act is not a confidence contest it might lose:
    /// when a person corrects a field, their act is the provenance of the new value even if the
    /// machine was more sure of the old one. That is what this method is for — the machine's
    /// pre-correction tag is preserved beneath the reviewer's, never replaced by it. Use
    /// <see cref="Merge"/> when two producers are genuinely competing.
    /// </para>
    /// </summary>
    public ProvenanceChain Append(ProvenanceTag newer)
    {
        ArgumentNullException.ThrowIfNull(newer);

        var prior = new List<ProvenanceTag>(Prior.Count + 1) { Current };
        prior.AddRange(Prior);
        return new ProvenanceChain(newer, prior);
    }

    /// <summary>
    /// Merge a candidate tag: the higher confidence wins, a tie breaks toward the more deterministic
    /// source, and <b>the loser is preserved</b> at the head of <see cref="Prior"/> either way.
    ///
    /// The comparison itself is <see cref="ProvenanceTag.Beats"/> — the framework's one
    /// implementation of the rule, so nothing here can drift from what the projection and the
    /// inference merge step decide.
    /// </summary>
    public ProvenanceChain Merge(ProvenanceTag candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Beats(Current))
            return Append(candidate);

        var updatedPrior = new List<ProvenanceTag>(Prior.Count + 1) { candidate };
        updatedPrior.AddRange(Prior);
        return new ProvenanceChain(Current, updatedPrior);
    }

    /// <summary>
    /// Appends all tags from <paramref name="other"/> after this chain's tags.
    /// <paramref name="other"/>'s <see cref="Current"/> becomes the new Current;
    /// <paramref name="other"/>'s Prior + this chain's tags form the new Prior,
    /// preserving newest-first ordering.
    /// </summary>
    public ProvenanceChain AppendChain(ProvenanceChain other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var newPrior = new List<ProvenanceTag>(other.Prior.Count + 1 + Prior.Count);
        newPrior.AddRange(other.Prior);
        newPrior.Add(Current);
        newPrior.AddRange(Prior);
        return new ProvenanceChain(other.Current, newPrior);
    }
}
