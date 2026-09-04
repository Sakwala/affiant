using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Serialization;

namespace Affiant.Core.Serialization;

/// <summary>
/// The canonical form of an Affidavit and its accepted amendments, and the SHA-256 over it.
///
/// <para>
/// <b>SR-1</b> — <i>the canonical form of a filed proposal is a deterministic byte sequence over the
/// accepted state (the amended Affidavit once an amendment is accepted, else the Affidavit as
/// proposed): UTF-8; object keys sorted by Unicode code point at every level; no insignificant
/// whitespace; numbers in shortest round-trip decimal form, always positional, <c>-0</c> as
/// <c>0</c>, non-finite refused; strings escaped only as JSON requires; <c>null</c> written, absent
/// omitted; money as its two strings (SR-2); an amended field's reviewer-act tag included in its
/// chain. <c>canonicalHash</c> is the SHA-256 of that form.</i>
/// </para>
///
/// <para>
/// <b>Why the amendments are inside the form and not beside it.</b> A host's execution grant binds
/// to the hash of what a reviewer accepted. If the form covered the proposal alone, a grant minted
/// for the record a reviewer <i>was shown</i> would still validate the record they <i>amended</i> —
/// the one substitution this framework exists to prevent. Two of the protocol's seven byte vectors
/// differ only by an amendment, and their hashes differ; that is the rule made checkable.
/// </para>
///
/// <para>
/// <b>Why a canonical form at all.</b> Three things depend on two independent implementations
/// agreeing on bytes: a conformance fixture compares canonical forms; an utterance-span binding
/// hashes the span it points at; and an execution grant hashes the accepted state. Any of them
/// breaks if .NET and TypeScript disagree about how to write the number <c>1.0</c> or where to put
/// the key <c>é</c>.
/// </para>
///
/// <para>
/// <b>Two entry points, and why both exist.</b> The typed overloads take an
/// <see cref="Affidavit"/> and are what a host calls. The <see cref="JsonNode"/> overloads take a
/// document — <i>any</i> document — and are what the protocol's byte vectors exercise, because two
/// of the seven are not Affidavits at all: one is a key-ordering stress case and one is a table of
/// number forms. The typed path is the document path with one extra step in front of it (serialize
/// through <see cref="AffiantJson"/>, then canonicalize the resulting JSON), so there is one
/// canonicaliser and not two.
/// </para>
///
/// <para>
/// <b>Synchronous, deliberately.</b> The TypeScript line hashes asynchronously because Web Crypto —
/// the only digest a package that must run on Node, Bun and workerd can reach — has no synchronous
/// digest (RT-1). That is a portability constraint of that runtime, not a property of the rule: the
/// conformance fixtures assert <i>values</i>, not call shapes, and .NET has a synchronous SHA-256.
/// </para>
/// </summary>
public static class CanonicalSerializer
{
    // ── The typed entry points ───────────────────────────────────────────────

    /// <summary>The canonical form of <paramref name="affidavit"/> as UTF-8 bytes (SR-1).</summary>
    /// <param name="affidavit">The record to canonicalize — the accepted state, if an amendment was accepted.</param>
    public static byte[] Canonicalize(Affidavit affidavit) =>
        Encoding.UTF8.GetBytes(CanonicalString(affidavit));

    /// <summary>
    /// The canonical form of <paramref name="affidavit"/> as a string — the same document
    /// <see cref="Canonicalize(Affidavit)"/> returns, one encoding step earlier.
    ///
    /// Useful where the bytes are not what is wanted: a fixture readable in a diff, a log line, a
    /// comparison in a test. The bytes are the contract; this is the same document before UTF-8.
    /// </summary>
    public static string CanonicalString(Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(affidavit);
        return CanonicalString(ToDocument(affidavit));
    }

    /// <summary>The SHA-256 of the canonical form, as 64 lowercase hexadecimal characters (SR-1).</summary>
    public static string CanonicalHash(Affidavit affidavit) =>
        Sha256Hex(Canonicalize(affidavit));

    /// <summary>
    /// The canonical form of <paramref name="affidavit"/> with <paramref name="amendments"/>
    /// accepted on it, as UTF-8 bytes (SR-1).
    ///
    /// <para>
    /// The amendments are folded in by <see cref="AffidavitAmendments.Apply"/> — the framework's one
    /// implementation of what an accepted correction does to the record — so these bytes and the
    /// amended record a Docket row keeps cannot disagree about the same decision. A null or empty
    /// map is the same as none.
    /// </para>
    /// </summary>
    /// <param name="affidavit">The Affidavit as filed.</param>
    /// <param name="amendments">
    /// The reviewer's accepted corrections, keyed by field name. A key holding a value sets the
    /// field; a key holding <c>null</c> clears it; an absent key leaves it untouched (DK-2).
    /// </param>
    /// <param name="entryId">The Docket entry the decision was made on.</param>
    /// <param name="decisionAt">When the decision was made (PV-2).</param>
    /// <param name="reviewerId">Who made it, as the host identifies them.</param>
    public static byte[] Canonicalize(
        Affidavit affidavit,
        IReadOnlyDictionary<string, object?>? amendments,
        Guid entryId,
        DateTimeOffset decisionAt,
        string reviewerId) =>
        Encoding.UTF8.GetBytes(CanonicalString(affidavit, amendments, entryId, decisionAt, reviewerId));

    /// <inheritdoc cref="Canonicalize(Affidavit, IReadOnlyDictionary{string, object}, Guid, DateTimeOffset, string)" />
    public static string CanonicalString(
        Affidavit affidavit,
        IReadOnlyDictionary<string, object?>? amendments,
        Guid entryId,
        DateTimeOffset decisionAt,
        string reviewerId) =>
        CanonicalString(AffidavitAmendments.Apply(affidavit, amendments, entryId, decisionAt, reviewerId));

    /// <inheritdoc cref="Canonicalize(Affidavit, IReadOnlyDictionary{string, object}, Guid, DateTimeOffset, string)" />
    public static string CanonicalHash(
        Affidavit affidavit,
        IReadOnlyDictionary<string, object?>? amendments,
        Guid entryId,
        DateTimeOffset decisionAt,
        string reviewerId) =>
        Sha256Hex(Canonicalize(affidavit, amendments, entryId, decisionAt, reviewerId));

    // ── The document entry points ────────────────────────────────────────────

    /// <summary>The canonical form of an arbitrary JSON document as UTF-8 bytes (SR-1).</summary>
    public static byte[] Canonicalize(JsonNode? document) =>
        Encoding.UTF8.GetBytes(CanonicalString(document));

    /// <summary>
    /// The canonical form of an arbitrary JSON document as a string (SR-1).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The document carries a non-finite number. JSON cannot spell one, so it has no canonical form;
    /// refusing is the only answer that does not invent a value nobody swore to.
    /// </exception>
    public static string CanonicalString(JsonNode? document)
    {
        var text = new StringBuilder();
        Write(document, text);
        return text.ToString();
    }

    /// <summary>The SHA-256 of an arbitrary document's canonical form, as lowercase hexadecimal.</summary>
    public static string CanonicalHash(JsonNode? document) => Sha256Hex(Canonicalize(document));

    /// <summary>
    /// SHA-256 over arbitrary bytes, as 64 lowercase hexadecimal characters.
    ///
    /// Public because the canonical form is not the only thing this framework hashes: an
    /// utterance-span binding hashes the span it points at so an auditor can re-derive it, and a
    /// host's execution grant hashes what it grants over.
    /// </summary>
    public static string Sha256Hex(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// <paramref name="affidavit"/> as the JSON document its canonical form is taken over: the
    /// protocol's record, written under <see cref="AffiantJson"/> and re-parsed.
    ///
    /// <para>
    /// The round trip through text is not waste. A confidence is a <see cref="float"/> in the CLR,
    /// and <c>0.9f</c> widened to a <see cref="double"/> is <c>0.89999997615814209</c> — so a
    /// canonicaliser that read the CLR value would write sixteen digits where the wire says
    /// <c>0.9</c>, and no second implementation could ever match it. The canonical form is defined
    /// over the document, so the document is what it reads.
    /// </para>
    ///
    /// <para>
    /// <b>The protocol's record, not this framework's.</b> SR-1 is a rule about a document two
    /// implementations must produce identically, and the rulebook's own byte vectors pin exactly ten
    /// properties on it. This framework's <see cref="Affidavit"/> carries four more — the warnings
    /// and the confirmation verdict, which the protocol keeps on the card envelope, and a field's
    /// closed set and pattern, which it keeps in the card's presentation hints — and spells the
    /// operation in its own four-valued vocabulary where the protocol's is two-valued and
    /// shape-shaped. Those four are dropped here and the operation is mapped, so a hash minted from
    /// a .NET record and a hash minted from a TypeScript one bind the same execution grant. Nothing
    /// is lost: what is dropped travels on the card, which carries all four.
    /// </para>
    /// </summary>
    public static JsonNode ToDocument(Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        var document = JsonNode.Parse(JsonSerializer.Serialize(affidavit, AffiantJson.SerializerOptions))!.AsObject();

        document.Remove("warnings");
        document.Remove("requiresConfirmation");

        // And the protocol version, which is the one property the rulebook's own two artifacts
        // disagree about. Its Affidavit schema REQUIRES `protocolVersion` on the record and its byte
        // vectors carry it; every conformance fixture that pins a content hash was produced by a
        // record that does not, so a form carrying it can never match one of those hashes. A hash is
        // what an execution grant binds to, so the canonical form follows the hashes. The record
        // still carries the version, and the wire still puts it on every envelope (SR-3).
        document.Remove("protocolVersion");
        document["operationType"] = Operation.IsUpdateShaped(affidavit.OperationType) ? "update" : "create";

        if (document["fields"] is JsonArray fields)
        {
            foreach (var field in fields.OfType<JsonObject>())
            {
                field.Remove("allowedValues");
                field.Remove("pattern");
            }
        }

        return document;
    }

    // ── Amendments, folded into a document rather than a record ──────────────

    /// <summary>
    /// Apply <paramref name="amendments"/> to a JSON <i>document</i> that is Affidavit-shaped,
    /// returning a new document: each amended field's value replaced and the reviewer's own tag put
    /// in force on its provenance chain, with the tag it supersedes preserved beneath it.
    ///
    /// <para>
    /// <b>When to reach for this rather than the typed overload.</b> Almost never: a host holding an
    /// <see cref="Affidavit"/> should call
    /// <see cref="CanonicalString(Affidavit, IReadOnlyDictionary{string, object}, Guid, DateTimeOffset, string)"/>,
    /// which folds the amendments through <see cref="AffidavitAmendments.Apply"/>. This overload
    /// exists for a document that did not come from this framework's model — a record read back from
    /// a store written by an older release, or a protocol byte vector — where re-typing it would
    /// silently rewrite properties the model has since renamed and produce different bytes for a
    /// document nobody changed.
    /// </para>
    ///
    /// <para>
    /// The tag in force comes from <see cref="AffidavitAmendments.AmendmentTag"/>, the same mint site
    /// the typed path uses, so the two cannot drift.
    /// </para>
    /// </summary>
    /// <param name="affidavit">An Affidavit-shaped JSON object carrying a <c>fields</c> array.</param>
    /// <param name="amendments">The reviewer's accepted corrections, keyed by field name (DK-2).</param>
    /// <param name="entryId">The Docket entry the decision was made on.</param>
    /// <param name="decisionAt">When the decision was made (PV-2).</param>
    /// <param name="reviewerId">Who made it, as the host identifies them.</param>
    /// <exception cref="ArgumentException">
    /// The document carries no <c>fields</c> array, or <paramref name="amendments"/> names a field it
    /// does not carry. An amendment to something nobody swore to is a caller's bug, and swallowing it
    /// would let two implementations disagree in silence.
    /// </exception>
    public static JsonObject ApplyAmendmentsForCanonical(
        JsonObject affidavit,
        IReadOnlyDictionary<string, object?>? amendments,
        Guid entryId,
        DateTimeOffset decisionAt,
        string reviewerId)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        if (amendments is null || amendments.Count == 0)
            return (JsonObject)affidavit.DeepClone();

        if (affidavit["fields"] is not JsonArray fields)
        {
            throw new ArgumentException(
                "SR-1: amendments are applied per field, so the document must carry a \"fields\" array.",
                nameof(affidavit));
        }

        var amended = new HashSet<string>(StringComparer.Ordinal);
        var next = new JsonArray();

        // The turn the amendment tag carries is the AFFIDAVIT's, not the amended field's.
        // A reviewer's correction belongs to the conversation the proposal was made in; the
        // displaced tag's own turn says when the machine produced the value it replaced, and
        // reusing it would date the person's act to the machine's turn. The typed path reads
        // Affidavit.ConversationTurn for the same reason, and AmendmentTurnTests pins the two
        // against a record that STATES a turn, so a drift between them fails a test rather than
        // producing a row and a hash that disagree about the same decision.
        var turn = TurnOf(affidavit);

        foreach (var element in fields)
        {
            if (element is not JsonObject field ||
                field["name"]?.GetValueKind() != JsonValueKind.String)
            {
                next.Add(element?.DeepClone());
                continue;
            }

            var name = field["name"]!.GetValue<string>();
            if (!amendments.TryGetValue(name, out var replacement))
            {
                next.Add(field.DeepClone());
                continue;
            }

            amended.Add(name);
            var cleared = replacement is null;

            // AF-1: a cleared optional field is a field the write no longer proposes, so it is
            // absent rather than present holding nothing. A cleared mandatory field stays, tagged
            // Empty at confidence 0 — the entity still requires it.
            var mandatory = field["isMandatory"]?.GetValueKind() == JsonValueKind.True;
            if (cleared && !mandatory) continue;

            var copy = (JsonObject)field.DeepClone();
            copy["value"] = replacement is null
                ? null
                : JsonSerializer.SerializeToNode(replacement, AffiantJson.SerializerOptions);

            var tag = AffidavitAmendments.AmendmentTag(
                cleared,
                entryId,
                decisionAt,
                reviewerId,
                turn);

            copy["provenance"] = Supersede(
                field["provenance"],
                JsonSerializer.SerializeToNode(tag, AffiantJson.SerializerOptions)!);

            next.Add(copy);
        }

        var unknown = amendments.Keys.Where(name => !amended.Contains(name)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"DK-2: the amendment map names field(s) [{string.Join(", ", unknown)}], which this " +
                "document does not carry. An amendment applies to a field that was sworn to; a key " +
                "with no field is a caller's bug, not an empty amendment.",
                nameof(amendments));
        }

        var result = (JsonObject)affidavit.DeepClone();
        result["fields"] = next;

        // AF-4: where the document carries the aggregate, it is recomputed over the amended fields.
        // A canonical form that kept the pre-correction number would let a grant bind to a record
        // whose own summary contradicts its fields. The two companions (AF-2) are recomputed the
        // same way, and only where the document already carries them: adding a property a document
        // did not have would change bytes nobody asked to change.
        if (result["aggregateConfidence"]?.GetValueKind() == JsonValueKind.Number)
            result["aggregateConfidence"] = Aggregate(next);

        if (result.ContainsKey("populatedConfidence"))
            result["populatedConfidence"] = Populated(next);

        if (result["emptyFieldCount"]?.GetValueKind() == JsonValueKind.Number)
            result["emptyFieldCount"] = EmptyCount(next);

        return result;
    }

    /// <summary>
    /// The conversation turn a document states, or <c>null</c> when it states none.
    /// </summary>
    /// <remarks>
    /// Read off the Affidavit rather than off a field: the turn on a tag says when that tag's
    /// producer made its claim, and an amendment's tag is a person's act in the conversation the
    /// proposal belongs to. A seed-shaped record carries no <c>conversationTurn</c> at all and
    /// yields <c>null</c>, which is what it means.
    /// </remarks>
    private static int? TurnOf(JsonObject affidavit) =>
        affidavit["conversationTurn"] is { } turn && turn.GetValueKind() == JsonValueKind.Number
            ? turn.GetValue<int>()
            : null;

    /// <summary>
    /// Put <paramref name="tag"/> in force on a provenance chain, preserving the tag it supersedes
    /// at the head of the history (PV-2, AF-4).
    /// </summary>
    private static JsonObject Supersede(JsonNode? chain, JsonNode tag)
    {
        if (chain is not JsonObject record)
            return new JsonObject { ["current"] = tag, ["prior"] = new JsonArray() };

        var next = (JsonObject)record.DeepClone();
        var prior = new JsonArray();
        if (next["current"] is { } superseded) prior.Add(superseded.DeepClone());
        if (next["prior"] is JsonArray existing)
        {
            foreach (var older in existing) prior.Add(older?.DeepClone());
        }

        next["current"] = tag;
        next["prior"] = prior;
        return next;
    }

    /// <summary>
    /// AF-2's aggregate over already-serialized fields: the minimum current confidence, an
    /// <c>Empty</c> tag counting as 0, and no proposed field at all counting as 0.
    ///
    /// Written out over the document rather than reached for from
    /// <see cref="AffidavitConfidence.Compute"/> because this path serializes whatever document it is
    /// handed, including shapes that are not core <see cref="AffidavitField"/>s. A field whose chain
    /// says nothing readable contributes 0: an unreadable grade is not evidence of a good one.
    /// </summary>
    private static JsonNode Aggregate(JsonArray fields)
    {
        if (fields.Count == 0) return JsonValue.Create(0);

        var lowest = 1d;
        foreach (var field in fields)
        {
            var confidence = ConfidenceOf(field) ?? 0d;
            if (confidence < lowest) lowest = confidence;
        }

        return JsonValue.Create(lowest);
    }

    private static JsonNode? Populated(JsonArray fields)
    {
        double? lowest = null;
        foreach (var field in fields)
        {
            if (IsEmptyTagged(field)) continue;
            var confidence = ConfidenceOf(field) ?? 0d;
            lowest = lowest is null ? confidence : Math.Min(lowest.Value, confidence);
        }

        return lowest is null ? null : JsonValue.Create(lowest.Value);
    }

    private static JsonNode EmptyCount(JsonArray fields) =>
        JsonValue.Create(fields.Count(IsEmptyTagged));

    private static bool IsEmptyTagged(JsonNode? field) =>
        field?["provenance"]?["current"]?["source"]?.GetValueKind() == JsonValueKind.String &&
        field["provenance"]!["current"]!["source"]!.GetValue<string>() == nameof(ProvenanceSource.Empty);

    private static double? ConfidenceOf(JsonNode? field)
    {
        if (IsEmptyTagged(field)) return 0d;

        var current = field?["provenance"]?["current"]?["confidence"];
        return current?.GetValueKind() == JsonValueKind.Number ? AsDouble(current) : null;
    }

    // ── The canonicaliser itself ─────────────────────────────────────────────

    private static void Write(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                return;

            case JsonObject o:
                text.Append('{');
                var first = true;
                foreach (var (key, value) in o.OrderBy(pair => pair.Key, CodePointComparer.Instance))
                {
                    if (!first) text.Append(',');
                    first = false;
                    WriteString(key, text);
                    text.Append(':');
                    Write(value, text);
                }

                text.Append('}');
                return;

            case JsonArray a:
                // Array order is data and is never sorted.
                text.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    Write(a[i], text);
                }

                text.Append(']');
                return;

            default:
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(node.GetValue<string>(), text);
                        return;
                    case JsonValueKind.Number:
                        text.Append(Number(AsDouble(node)));
                        return;
                    case JsonValueKind.True:
                        text.Append("true");
                        return;
                    case JsonValueKind.False:
                        text.Append("false");
                        return;
                    default:
                        text.Append("null");
                        return;
                }
        }
    }

    /// <summary>
    /// The shortest decimal that round-trips, written <b>positionally</b>. One spelling per value.
    ///
    /// <para>
    /// Positional rather than exponential is the decision a naive implementation gets wrong:
    /// <c>1e21</c> is written out in full, because SR-1 says shortest round-trip <i>decimal</i> and
    /// two implementations have to agree about digits, not about one language's exponent threshold.
    /// Negative zero is written <c>0</c> — JSON has no negative zero and a reader cannot see the
    /// sign. And <c>0.1 + 0.2</c> is written with all seventeen digits of
    /// <c>0.30000000000000004</c>: rounding it would be the framework deciding what was sworn to.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is not finite.</exception>
    public static string Number(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException(
                $"SR-1: {value.ToString(CultureInfo.InvariantCulture)} has no canonical form — JSON " +
                "has no such number. A record cannot swear to a value it cannot write down.");
        }

        if (value == 0d) return "0";

        var round = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = round.IndexOfAny(['E', 'e']);
        if (exponentAt < 0) return round;

        var exponent = int.Parse(round[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        var mantissa = round[..exponentAt];
        var negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];

        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var pointAt = (point < 0 ? mantissa.Length : point) + exponent;

        string positional;
        if (pointAt <= 0)
            positional = "0." + new string('0', -pointAt) + digits;
        else if (pointAt >= digits.Length)
            positional = digits + new string('0', pointAt - digits.Length);
        else
            positional = digits[..pointAt] + "." + digits[pointAt..];

        return negative ? "-" + positional : positional;
    }

    /// <summary>
    /// Escapes only what JSON requires: a quote, a backslash and the C0 controls — with the
    /// two-character forms where JSON has them and lowercase <c>\uXXXX</c> otherwise. A solidus is
    /// never escaped and every non-ASCII character is written as itself, so an e-acute is two UTF-8
    /// bytes rather than six ASCII ones.
    /// </summary>
    private static void WriteString(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        text.Append(c);
                    break;
            }
        }

        text.Append('"');
    }

    /// <summary>
    /// A JSON number as a <see cref="double"/>, whatever CLR numeric type the node happens to hold.
    /// A node built from a <see cref="float"/> refuses <c>GetValue&lt;double&gt;</c> outright, so
    /// every numeric read goes through here.
    /// </summary>
    private static double AsDouble(JsonNode node)
    {
        var value = node.AsValue();
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<float>(out var f)) return f;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<int>(out var i)) return i;
        return value.TryGetValue<decimal>(out var m)
            ? (double)m
            : throw new InvalidOperationException(
                $"SR-1: {node.ToJsonString()} is a JSON number this canonicaliser cannot read as a double.");
    }

    /// <summary>
    /// Orders strings by Unicode <b>code point</b>, which is not what an ordinal UTF-16 comparison
    /// does above the Basic Multilingual Plane.
    ///
    /// A comparator over UTF-16 code units sees an emoji's leading surrogate (U+D83D for U+1F600)
    /// and sorts it before a private-use character at U+E000 — the wrong way round, since
    /// 0xE000 &lt; 0x1F600. The protocol's key-ordering vector exists for exactly this.
    /// </summary>
    private sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                var cx = char.ConvertToUtf32(x, i);
                var cy = char.ConvertToUtf32(y, j);
                if (cx != cy) return cx.CompareTo(cy);

                i += char.IsSurrogatePair(x, i) ? 2 : 1;
                j += char.IsSurrogatePair(y, j) ? 2 : 1;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
