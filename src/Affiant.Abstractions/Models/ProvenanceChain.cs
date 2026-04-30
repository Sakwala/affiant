namespace Affiant.Abstractions.Models;

/// <summary>
/// Ordered history of provenance tags for a single field. The framework's audit
/// trail — answers "how did this field arrive at its current value?" by walking
/// the chain from <see cref="Current"/> through <see cref="Prior"/> (newest first).
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
    /// Append a newer tag unconditionally — the incoming tag becomes
    /// <see cref="Current"/> and the previous <see cref="Current"/> is prepended
    /// to <see cref="Prior"/> (preserving newest-first ordering).
    /// </summary>
    public ProvenanceChain Append(ProvenanceTag newer)
    {
        var prior = new List<ProvenanceTag>(Prior.Count + 1) { Current };
        prior.AddRange(Prior);
        return new ProvenanceChain(newer, prior);
    }

    /// <summary>
    /// Merge a candidate tag using the framework spec §2.3 merge rule: higher
    /// confidence wins; ties are broken by the determinism hierarchy encoded in
    /// <see cref="ProvenanceSource"/>'s enum ordinal (lower wins).
    /// </summary>
    public ProvenanceChain Merge(ProvenanceTag candidate)
    {
        var candidateWins =
            candidate.Confidence > Current.Confidence ||
            (candidate.Confidence == Current.Confidence &&
             (int)candidate.Source < (int)Current.Source);

        if (candidateWins)
        {
            var prior = new List<ProvenanceTag>(Prior.Count + 1) { Current };
            prior.AddRange(Prior);
            return new ProvenanceChain(candidate, prior);
        }

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
        var newPrior = new List<ProvenanceTag>(other.Prior.Count + 1 + Prior.Count);
        newPrior.AddRange(other.Prior);
        newPrior.Add(Current);
        newPrior.AddRange(Prior);
        return new ProvenanceChain(other.Current, newPrior);
    }
}
