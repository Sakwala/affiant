namespace Affiant.Abstractions.Models;

/// <summary>
/// The two checks that hold back a write approved with no person present and depend on nothing a
/// host supplies — a field the entity requires with no known value (protocol rule GT-5), and a
/// provenance grade the policy predicates on that points at nothing (protocol rule PV-4) — as pure
/// reads of an <see cref="Affidavit"/>.
///
/// <para>
/// One copy, three callers, for the same reason <see cref="AffidavitSubstance"/> has one: the
/// framework's Standing Order base class runs them before it spends a host's risk scorer, the
/// approval-policy chain runs them again over any verdict that reaches it from a policy written
/// against the bare interface, and a fixture asks them directly without staging a policy at all. A
/// second copy would drift, and the shape of the drift would be that one of the three stopped
/// holding a write back.
/// </para>
///
/// <para>
/// The third check — the host risk score against the policy's declared ceiling — is not here: it
/// needs a host-supplied scorer, and the framework owns only the comparison (GT-5). It lives with
/// the Standing Order base class that owns the ceiling.
/// </para>
/// </summary>
public static class StandingOrderGuard
{
    /// <summary>
    /// The proposed fields marked <see cref="AffidavitField.IsMandatory"/> whose tag in force is
    /// <see cref="ProvenanceSource.Empty"/> — a field the write cannot do without, sworn to with no
    /// known value — in the order the Affidavit lists them. Empty when there are none.
    ///
    /// <para>
    /// This is the one hole a confidence number cannot describe.
    /// <see cref="AffidavitConfidence.AggregateConfidence"/> is already 0 whenever any proposed
    /// field is <c>Empty</c>, and a host that keys its Standing Order on
    /// <see cref="AffidavitConfidence.PopulatedConfidence"/> — the minimum over the fields that
    /// <em>were</em> filled — reads a high number over a proposal missing something the write
    /// needs. PV-4 cannot reach the case either, because <c>Empty</c> sits at the bottom of the
    /// ladder rather than above <see cref="ProvenanceSource.Conversation"/>. So the rule is
    /// structural, whatever the numbers say. A person may still approve — they can see the hole,
    /// and approving is of what was sworn to, not a licence to invent the missing value.
    /// </para>
    ///
    /// <para>
    /// An <em>optional</em> field left <c>Empty</c> does not hold a Standing Order back by rule. A
    /// host that wants it to predicates its own policy on <c>PopulatedConfidence</c> or
    /// <c>EmptyFieldCount</c>, which is where a floor belongs: this framework defines no threshold
    /// on any of the three numbers.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> EmptyMandatoryFields(Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        List<string>? found = null;
        foreach (var field in affidavit.Fields)
        {
            if (!field.IsMandatory) continue;
            if (field.Provenance.Current.Source != ProvenanceSource.Empty) continue;
            (found ??= []).Add(field.Name);
        }

        return found ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// The first proposed field whose tag in force names one of <paramref name="declaredInputs"/>,
    /// sits above <see cref="ProvenanceSource.Conversation"/>, and carries no
    /// <see cref="ProvenanceBinding"/> — or <see langword="null"/> when every declared input points
    /// at something an auditor could check (PV-4).
    ///
    /// <para>
    /// PV-4 asks a question the Affidavit alone cannot answer: <em>did this verdict depend on a
    /// grade a caller could have asserted with nothing behind it?</em> A policy that predicates only
    /// on field values, on host state, or on tags at or below <c>Conversation</c> is unaffected —
    /// the turn is its own artifact. A policy that predicates on <c>UserStated</c>, <c>External</c>
    /// or <c>Computed</c> is claiming an artifact outside the conversation, and the check is that
    /// the artifact is actually pointed at. So the policy declares what it predicates on, and this
    /// checks the declaration against the tags in force.
    /// </para>
    ///
    /// <para>
    /// Only the tag <b>in force</b> on each field is checked, never the superseded tags behind it in
    /// the chain: a verdict rests on the values the Affidavit currently swears to. The displaced
    /// tags stay on the record for a reviewer to read.
    /// </para>
    /// </summary>
    public static UnboundDeclaredInput? FirstUnboundDeclaredInput(
        Affidavit affidavit,
        IReadOnlyCollection<ProvenanceSource> declaredInputs)
    {
        ArgumentNullException.ThrowIfNull(affidavit);
        ArgumentNullException.ThrowIfNull(declaredInputs);

        if (declaredInputs.Count == 0) return null;

        foreach (var field in affidavit.Fields)
        {
            var tag = field.Provenance.Current;
            if (!declaredInputs.Contains(tag.Source)) continue;
            if (!ProvenanceTag.RequiresBinding(tag.Source)) continue;
            if (tag.IsBound) continue;
            return new UnboundDeclaredInput(field.Name, tag.Source);
        }

        return null;
    }

    /// <summary>
    /// The one-line reason a mandatory-<c>Empty</c> degrade puts on the reviewer's card. Names
    /// field <em>names</em>, which are schema, and never a field <em>value</em>.
    /// </summary>
    public static string MandatoryFieldEmptyReason(IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var names = string.Join(", ", fields.Select(f => $"\"{f}\""));
        var one = fields.Count == 1;
        return
            $"GT-5: {names} {(one ? "is a field" : "are fields")} this write requires and " +
            $"{(one ? "it has" : "they have")} no known value; a Standing Order does not fire over " +
            "an empty required field, so a person is asked instead.";
    }

    /// <summary>The one-line reason a PV-4 degrade puts on the reviewer's card.</summary>
    public static string UnboundDeclaredInputReason(UnboundDeclaredInput unbound)
    {
        ArgumentNullException.ThrowIfNull(unbound);
        return
            $"PV-4: this Standing Order predicates on {unbound.Source} provenance, and " +
            $"\"{unbound.Field}\" carries a {unbound.Source} tag pointing at nothing an auditor " +
            "could re-check; a person is asked instead.";
    }
}

/// <summary>
/// The field and grade that failed PV-4: a tag the winning policy declared it predicates on, graded
/// above <see cref="ProvenanceSource.Conversation"/>, pointing at nothing.
/// </summary>
/// <param name="Field">The field carrying the tag.</param>
/// <param name="Source">The grade the tag claims.</param>
public sealed record UnboundDeclaredInput(string Field, ProvenanceSource Source);
