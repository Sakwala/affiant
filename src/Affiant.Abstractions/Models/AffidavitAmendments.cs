namespace Affiant.Abstractions.Models;

/// <summary>
/// What a reviewer's accepted correction does to an <see cref="Affidavit"/> — the one
/// implementation, so a host's write executor, the review gate and a serializer cannot each fold
/// an amendment in a slightly different way.
///
/// <para>
/// <b>The amendment map's two meanings.</b> A key present holding a value <em>sets</em> the field;
/// a key present holding <c>null</c> <em>clears</em> it; a key that is absent leaves the field
/// untouched. Collapsing "cleared" into "untouched" either wipes a field nobody asked to wipe or
/// drops a correction a person made on purpose, so the two are never conflated here.
/// </para>
///
/// <para>
/// <b>What an accepted amendment does to the numbers.</b> The three confidence numbers are
/// recomputed over the amended fields. Before this existed, they were computed once at filing and
/// never again, so after a human corrected exactly what the model got wrong the record still
/// reported the model's original, pre-correction confidence.
/// </para>
///
/// <para>
/// <b>Why a clear is resolved against the field rather than pasted onto it.</b> A cleared field has
/// no value, so it cannot have confidence in one. Writing the reviewer's maximal tag over an
/// emptied field would make the numbers <em>rise</em> as a reviewer wiped the record — clear every
/// field and it would report perfect confidence over nothing. So a cleared <b>mandatory</b> field
/// stays present and reads <see cref="ProvenanceSource.Empty"/> at confidence 0 (the entity still
/// requires it, so it is still proposed and still on the card, now visibly with nothing behind it),
/// and a cleared <b>optional</b> field leaves the field list entirely (a reviewer clearing an
/// optional field is saying "do not write this one", which is a field the write no longer
/// proposes). Either way the reviewer's act is not lost: the tag carries the same
/// <see cref="ProvenanceBinding.ReviewerAct"/> binding a set would.
/// </para>
/// </summary>
public static class AffidavitAmendments
{
    /// <summary>
    /// Apply <paramref name="amendments"/> to <paramref name="affidavit"/> as the decision made on
    /// Docket entry <paramref name="entryId"/>, returning a new Affidavit with the amended fields,
    /// the reviewer's provenance on top of each amended field's chain, and all three confidence
    /// numbers recomputed.
    ///
    /// <para>
    /// The proposal is <b>not</b> modified: this returns the amended record beside it, so the card a
    /// person actually approved and the record the write is performed from are both readable after
    /// the fact.
    /// </para>
    ///
    /// <para>
    /// An amended field's chain keeps everything: the reviewer's tag goes <em>on top</em> rather
    /// than merged, because a person's correction is not a confidence contest it might lose to the
    /// machine's own tag, and the machine's displaced tag stays beneath it so the card can still
    /// show what was proposed. A field's <see cref="AffidavitField.PreviousValue"/> never moves — it
    /// is what the entity holds now, which an amendment does not change.
    /// </para>
    /// </summary>
    /// <param name="affidavit">The filed proposal.</param>
    /// <param name="amendments">
    /// The reviewer's corrections, keyed by <see cref="AffidavitField.Name"/>. Null or empty returns
    /// <paramref name="affidavit"/> unchanged.
    /// </param>
    /// <param name="entryId">The Docket entry the decision was made on.</param>
    /// <param name="decisionAt">
    /// When the decision was made. Passed in rather than read from a clock, so a fixture can pin it.
    /// </param>
    /// <param name="reviewerId">Who made the decision, as the host identifies them.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="amendments"/> names a field the Affidavit does not propose. That is a
    /// caller's programming error — an out-of-range name — rather than a refusal about the
    /// proposal's substance, so it is an argument exception and not a gate refusal code.
    /// </exception>
    public static Affidavit Apply(
        Affidavit affidavit,
        IReadOnlyDictionary<string, object?>? amendments,
        Guid entryId,
        DateTimeOffset decisionAt,
        string reviewerId)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        if (amendments is null || amendments.Count == 0)
            return affidavit;

        var unknown = amendments.Keys
            .Where(name => !affidavit.Fields.Any(f => string.Equals(f.Name, name, StringComparison.Ordinal)))
            .ToArray();

        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Amendment names field(s) [{string.Join(", ", unknown)}], which this Affidavit does " +
                $"not propose. An Affidavit's fields are exactly the fields the operation proposes; " +
                "amending one it does not carry would silently widen the write a reviewer approved.",
                nameof(amendments));
        }

        var fields = new List<AffidavitField>(affidavit.Fields.Length);
        foreach (var field in affidavit.Fields)
        {
            if (!amendments.TryGetValue(field.Name, out var amended))
            {
                // Untouched: the field keeps its value, its previous value and its whole chain.
                fields.Add(field);
                continue;
            }

            var cleared = amended is null;

            // A cleared optional field is a field the write no longer proposes, so it is absent
            // rather than present with nothing in it.
            if (cleared && !field.IsMandatory)
                continue;

            // The turn is the AFFIDAVIT's, never the amended field's: a reviewer's correction
            // belongs to the conversation the proposal was made in, and the displaced tag's own turn
            // says when the machine produced the value being replaced. The rulebook's amended vector
            // pins it — the record states turn 3, the displaced tag states none, and the minted tag
            // carries 3.
            var tag = AmendmentTag(
                cleared,
                entryId,
                decisionAt,
                reviewerId,
                affidavit.ConversationTurn);

            fields.Add(field with
            {
                Value = amended,
                Provenance = field.Provenance.Append(tag),
            });
        }

        return affidavit.WithFields([.. fields]);
    }

    /// <summary>
    /// The tag an accepted amendment puts in force on the field it touched — the framework's one
    /// definition of what a reviewer's correction is, as provenance.
    ///
    /// <para>
    /// It is public and separate from <see cref="Apply"/> because two paths mint it and they must
    /// not drift: <see cref="Apply"/>, which produces the amended record a Docket row keeps, and the
    /// canonical serializer, which produces the bytes an execution grant binds to (SR-1). If those
    /// two ever minted slightly different tags, the row and the hash would disagree about the same
    /// decision — and the hash is what the grant is checked against, so the row would lose.
    /// </para>
    ///
    /// <para>
    /// A <b>set</b> is a <see cref="ProvenanceSource.UserStated"/> tag at confidence 1: a person
    /// typed the value, which is the strongest grade there is. A <b>clear</b> is
    /// <see cref="ProvenanceSource.Empty"/> at confidence 0, because a cleared field has no value
    /// and so cannot have confidence in one — writing the reviewer's maximal tag over an emptied
    /// field would make the three numbers <i>rise</i> as a reviewer wiped the record. Either way the
    /// tag carries the same <see cref="ProvenanceBinding.ReviewerAct"/> binding, which names the
    /// entry and the instant (PV-2), and <see cref="ProvenanceTag.At"/> is that instant.
    /// </para>
    /// </summary>
    /// <param name="cleared">Whether the reviewer cleared the field rather than setting it.</param>
    /// <param name="entryId">The Docket entry the decision was made on.</param>
    /// <param name="decisionAt">
    /// When the decision was made. Passed in rather than read from a clock, so a fixture can pin it.
    /// </param>
    /// <param name="reviewerId">Who made the decision, as the host identifies them.</param>
    /// <param name="conversationTurn">
    /// The turn the AFFIDAVIT was made on. A reviewer's correction belongs to the conversation the
    /// proposal belongs to; the displaced tag's own turn says when the machine produced the value
    /// being replaced, and reusing it would date a person's act to the machine's turn.
    /// </param>
    public static ProvenanceTag AmendmentTag(
        bool cleared,
        Guid entryId,
        DateTimeOffset decisionAt,
        string reviewerId,
        int? conversationTurn)
    {
        var binding = new ProvenanceBinding.ReviewerAct(new ReviewerActRef(entryId, decisionAt));

        return cleared
            ? new ProvenanceTag(
                ProvenanceSource.Empty,
                0f,
                $"Cleared by {reviewerId} on Docket entry {entryId}",
                conversationTurn,
                binding,
                decisionAt)
            : new ProvenanceTag(
                ProvenanceSource.UserStated,
                1.0f,
                $"Amended by {reviewerId} on Docket entry {entryId}",
                conversationTurn,
                binding,
                decisionAt);
    }
}
