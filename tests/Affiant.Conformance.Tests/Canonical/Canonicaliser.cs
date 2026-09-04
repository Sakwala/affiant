using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Affiant.Conformance.Tests.Canonical;

/// <summary>
/// The canonical form of SR-1, written out from the rule: every key sorted by Unicode code point at
/// every level, no insignificant whitespace, one spelling per value.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the driver's own canonicaliser, not the framework's.</b> <c>1.0.0-beta.1</c> exports
/// no canonical-hash helper, so there is nothing in the shipped packages to call. <c>RUNNER.md</c>
/// §9 names three paths that have to agree on a byte vector — the implementation, a second
/// canonicaliser written out from the rule, and an off-the-shelf SHA-256 — and this is the second
/// of them. A vector this reproduces is evidence about the rule's text and about whether the .NET
/// model can hold the shape; it is <b>not</b> a statement that the framework passes SR-1, and the
/// run log says so in as many words.
/// </para>
/// <para>
/// Three decisions the rule forces and a naive serializer gets wrong:
/// <list type="bullet">
/// <item>Keys sort by <b>code point</b>. A comparator over UTF-16 code units puts an emoji
/// (U+1F600, leading surrogate 0xD83D) before a private-use character (U+E000) — the wrong way
/// round. <c>canonical/key-order-stress</c> exists for exactly this.</item>
/// <item>Numbers are the shortest decimal that round-trips, written <b>positionally</b>: 1e21 is
/// written out in full rather than as <c>1e+21</c>, and negative zero is written <c>0</c>.</item>
/// <item>Strings escape only what JSON requires — a quote, a backslash and the C0 controls, with
/// the two-character forms where JSON has them and lowercase <c>\uXXXX</c> otherwise. A solidus is
/// never escaped and non-ASCII is written as itself.</item>
/// </list>
/// </para>
/// </remarks>
internal static class Canonicaliser
{
    /// <summary>The canonical UTF-8 text of a JSON document.</summary>
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    /// <summary>The SHA-256 of the canonical bytes, as 64 lowercase hex characters.</summary>
    public static string Sha256Hex(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;

            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in o.OrderBy(kv => kv.Key, CodePointComparer.Instance))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(key, sb);
                    sb.Append(':');
                    Write(value, sb);
                }

                sb.Append('}');
                return;

            case JsonArray a:
                // Array order is data and is never sorted.
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    Write(a[i], sb);
                }

                sb.Append(']');
                return;

            default:
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(node.GetValue<string>(), sb);
                        return;
                    case JsonValueKind.Number:
                        sb.Append(Number(Matching.Matcher.AsDouble(node)));
                        return;
                    case JsonValueKind.True:
                        sb.Append("true");
                        return;
                    case JsonValueKind.False:
                        sb.Append("false");
                        return;
                    default:
                        sb.Append("null");
                        return;
                }
        }
    }

    /// <summary>The shortest decimal that round-trips, written positionally. One spelling per value.</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidOperationException($"{value} has no canonical form: JSON has no such number.");
        }

        if (value == 0d)
        {
            return "0";
        }

        var round = value.ToString("R", CultureInfo.InvariantCulture);
        var e = round.IndexOfAny(['E', 'e']);
        if (e < 0)
        {
            return round;
        }

        var exponent = int.Parse(round[(e + 1)..], CultureInfo.InvariantCulture);
        var mantissa = round[..e];
        var negative = mantissa.StartsWith('-');
        if (negative)
        {
            mantissa = mantissa[1..];
        }

        var point = mantissa.IndexOf('.');
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var pointAt = (point < 0 ? mantissa.Length : point) + exponent;

        string text;
        if (pointAt <= 0)
        {
            text = "0." + new string('0', -pointAt) + digits;
        }
        else if (pointAt >= digits.Length)
        {
            text = digits + new string('0', pointAt - digits.Length);
        }
        else
        {
            text = digits[..pointAt] + "." + digits[pointAt..];
        }

        return negative ? "-" + text : text;
    }

    private static void WriteString(string value, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>Orders strings by Unicode code point, which is not what an ordinal UTF-16 comparison does above the BMP.</summary>
    private sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                var cx = char.ConvertToUtf32(x, i);
                var cy = char.ConvertToUtf32(y, j);
                if (cx != cy)
                {
                    return cx.CompareTo(cy);
                }

                i += char.IsSurrogatePair(x, i) ? 2 : 1;
                j += char.IsSurrogatePair(y, j) ? 2 : 1;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
