namespace Affiant.Core.Services;

using System.Globalization;
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
/// <i>hit</i> is an occurrence of the value text in the utterance under the case-insensitive
/// comparison below, whose neighbouring code points are absent or are none of: a letter (L*), a mark
/// (M*), a decimal digit (Nd), connector punctuation (Pc). The first hit wins unless the port
/// supplied a span that is itself a hit. Whitespace-only or empty value text never hits.
/// </para>
/// <para>
/// <b>The case table.</b> Two code points match when they are equal or their <i>simple</i> uppercase
/// mappings are equal — the single-code-point mapping, so a fold never changes length, no culture is
/// consulted, nothing is normalised and no character is ignorable. That is exactly
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, which is why no other comparison appears in
/// this file. Full case mappings are not applied: <c>STRAẞE</c> does not match <c>Straße</c>, and a
/// ligature does not match the letters it draws.
/// </para>
/// <para>
/// Offsets and lengths are in UTF-16 code units, which is what the <c>utterance-span</c> binding
/// records. A neighbouring character is a code point, not a code unit: a surrogate pair is read as
/// the one character it is, so a value abutting a letter outside the Basic Multilingual Plane is not
/// a hit for the same reason it would not be beside an ASCII letter. A combining mark blocks a hit
/// too — the grapheme the utterance draws there is not the word the value spells (<c>Cafe</c> inside
/// a decomposed <c>Café</c>), and <c>_</c> blocks one because it joins an identifier
/// (<c>WZ_BRN</c>).
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
    /// The text the span digest is taken over: a string is itself, a number its SR-1 canonical
    /// rendering, a boolean <c>true</c> or <c>false</c>.
    /// </param>
    /// <param name="hintedOffset">An offset the port reported, or <see langword="null"/>.</param>
    /// <param name="hintedLength">A length the port reported, or <see langword="null"/>.</param>
    public static Span? Locate(string utterance, string valueText, int? hintedOffset, int? hintedLength)
    {
        ArgumentNullException.ThrowIfNull(utterance);

        if (string.IsNullOrWhiteSpace(valueText))
            return null;

        // The port's span, when it is itself a hit: the port names an occurrence, and the framework
        // accepts it only on the terms it would have accepted its own — the utterance there says
        // what the port said it says, and the neighbours leave it a whole token. A span that is not
        // a hit is discarded and the finder runs from the start, so a port cannot bind a value to a
        // fragment of a longer token ("20" inside "2026-09-08") the finder itself refuses.
        if (hintedOffset is { } offset
            && hintedLength is { } length
            && offset >= 0
            && length >= 0
            && offset <= utterance.Length - length
            && utterance.AsSpan(offset, length).Equals(valueText, StringComparison.OrdinalIgnoreCase)
            && IsWholeToken(utterance, offset, length))
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
    /// code point that is none of L*, M*, Nd, Pc.
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

    /// <summary>
    /// A code point that does not join what stands beside it into one word. The four categories are
    /// the rule's, read the same way in both implementations:
    /// <see cref="CharUnicodeInfo.GetUnicodeCategory(string, int)"/> reads a surrogate pair as the
    /// one code point it is.
    /// </summary>
    private static bool IsBoundary(string utterance, int index) =>
        CharUnicodeInfo.GetUnicodeCategory(utterance, index) is not (
            UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation);
}
