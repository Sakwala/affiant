namespace Affiant.Abstractions.Models;

/// <summary>
/// The substance rule (protocol rule GT-3) as one predicate: does this Affidavit swear to anything?
///
/// <para>
/// The founding incident this exists for: an implementation can pass every structural test — the
/// Affidavit has the right shape, the right field names, the right envelope — while every field it
/// carries swears to nothing, so a proposal that knows nothing reaches a reviewer looking exactly
/// like one that knows everything. Three signatures say it happened:
/// </para>
/// <list type="number">
/// <item>A field asserts a value while its provenance reads <see cref="ProvenanceSource.Empty"/> —
/// the hollow signature: the field claims something and swears nothing about where it came
/// from.</item>
/// <item>No field carries provenance other than <see cref="ProvenanceSource.Empty"/>.</item>
/// <item>There are no fields at all.</item>
/// </list>
///
/// <para>
/// One copy, three callers: the projection reports it as telemetry, the compliance harness asserts
/// it at test time, and the gate refuses on it at run time. A second copy of this predicate would
/// drift, and the shape of the drift would be that one of the three stopped catching the incident.
/// </para>
/// </summary>
public static class AffidavitSubstance
{
    /// <summary>
    /// Why <paramref name="affidavit"/> swears to nothing, as a sentence, or <see langword="null"/>
    /// when it swears to something.
    ///
    /// <para>
    /// The sentence names field <em>names</em>, which are schema, and never a field <em>value</em>,
    /// which is the user's data: it is safe to put on a telemetry event and in a refusal message.
    /// </para>
    /// </summary>
    public static string? DescribeFailure(Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        if (affidavit.Fields.Length == 0)
            return "the Affidavit swears to no fields";

        foreach (var field in affidavit.Fields)
        {
            if (field.Provenance.Current.Source != ProvenanceSource.Empty) continue;
            if (field.Value is null) continue;
            if (field.Value is string text && string.IsNullOrWhiteSpace(text)) continue;

            return $"field \"{field.Name}\" carries a value with Empty provenance";
        }

        return affidavit.Fields.All(f => f.Provenance.Current.Source == ProvenanceSource.Empty)
            ? "no proposed field carries provenance other than Empty"
            : null;
    }

    /// <summary>Whether <paramref name="affidavit"/> swears to something.</summary>
    public static bool IsSubstantive(Affidavit affidavit) => DescribeFailure(affidavit) is null;
}
