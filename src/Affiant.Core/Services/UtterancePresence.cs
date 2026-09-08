namespace Affiant.Core.Services;

using System.Text;

/// <summary>
/// Where a value's text sits in the turn a person typed, if it sits there at all (PV-3).
/// </summary>
/// <remarks>
/// <para>
/// PV-3 grades an inferred field <c>Conversation</c> when "the value is literally present in the
/// utterance". That is a property of two strings, so the framework establishes it by looking, and a
/// port's own <c>presence</c> claim is a hint that is verified the same way or not honoured at all.
/// </para>
/// <para>
/// <b>The rule, stated once so a second implementation can implement the same sentence.</b> A
/// <i>hit</i> is an occurrence of the value text in the utterance under an ordinal, case-insensitive
/// comparison, whose neighbouring characters are absent or are neither letters nor digits (Unicode
/// categories L* and Nd). The first hit wins unless the port supplied a span that verifies — the
/// utterance at that span equals the value text under the same comparison. Whitespace-only or empty
/// value text never hits.
/// </para>
/// <para>
/// Offsets and lengths are in UTF-16 code units, which is what the <c>utterance-span</c> binding
/// records. A neighbouring character is a code point, not a code unit: a surrogate pair is read as
/// the one character it is, so a value abutting a letter outside the Basic Multilingual Plane is not
/// a hit for the same reason it would not be beside an ASCII letter.
/// </para>
/// </remarks>
internal static class UtterancePresence
{
    /// <summary>An occurrence of the value text in the utterance, in UTF-16 code units.</summary>
    internal readonly record struct Span(int Offset, int Length);

    /// <summary>
    /// The span of <paramref name="utterance"/> the value was read from, or <see langword="null"/>
    /// when the value is not literally present.
    /// </summary>
    /// <param name="utterance">The current turn's user text, unmodified.</param>
    /// <param name="valueText">
    /// The text the span digest is taken over: the string itself, or the raw JSON token for a number
    /// or a boolean.
    /// </param>
    /// <param name="hintedOffset">An offset the port reported, or <see langword="null"/>.</param>
    /// <param name="hintedLength">A length the port reported, or <see langword="null"/>.</param>
    public static Span? Locate(string utterance, string valueText, int? hintedOffset, int? hintedLength)
    {
        ArgumentNullException.ThrowIfNull(utterance);

        if (string.IsNullOrWhiteSpace(valueText))
            return null;

        // The port's span, when it verifies against the text: the port names an occurrence, the
        // framework checks that the utterance at that place says what the port said it says. A span
        // that does not verify is discarded rather than trusted — the whole point of D1.
        if (hintedOffset is { } offset
            && hintedLength is { } length
            && offset >= 0
            && length >= 0
            && offset <= utterance.Length - length
            && utterance.AsSpan(offset, length).Equals(valueText, StringComparison.OrdinalIgnoreCase))
        {
            return new Span(offset, length);
        }

        for (var from = 0; from <= utterance.Length - valueText.Length;)
        {
            var at = utterance.IndexOf(valueText, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return null;

            if (IsWholeToken(utterance, at, valueText.Length))
                return new Span(at, valueText.Length);

            from = at + 1;
        }

        return null;
    }

    /// <summary>
    /// SHA-256 over the UTF-8 bytes of the utterance at <paramref name="span"/>, as 64 lowercase
    /// hexadecimal characters — the canonical form's own digest (SR-1, PV-2). It is taken over what
    /// the utterance says, not over what the port reported, so the two differ wherever case does.
    /// </summary>
    public static string DigestOf(string utterance, Span span) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(utterance.Substring(span.Offset, span.Length))));

    /// <summary>
    /// Whether the occurrence at <paramref name="at"/> is bounded on both sides: nothing there, or a
    /// character that is neither a letter nor a digit.
    /// </summary>
    private static bool IsWholeToken(string utterance, int at, int length) =>
        IsBoundaryBefore(utterance, at) && IsBoundaryAfter(utterance, at + length);

    private static bool IsBoundaryBefore(string utterance, int at)
    {
        if (at == 0)
            return true;

        // A code point, not a code unit: step back onto the high surrogate so the pair is read as
        // the single character it is.
        var index = at - 1;
        if (char.IsLowSurrogate(utterance[index]) && index > 0 && char.IsHighSurrogate(utterance[index - 1]))
            index--;

        return IsBoundary(utterance, index);
    }

    private static bool IsBoundaryAfter(string utterance, int end) =>
        end >= utterance.Length || IsBoundary(utterance, end);

    private static bool IsBoundary(string utterance, int index) =>
        !char.IsLetter(utterance, index) && !char.IsDigit(utterance, index);
}
