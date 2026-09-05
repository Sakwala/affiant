using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;

namespace Affiant.EntityFramework.Stores;

/// <summary>
/// The one place the composite facts on a <see cref="DocketEntry"/> — the attestation, the blocked
/// marker, the decision record and a preserved late amendment map — cross the boundary between the
/// row and a JSON column.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than left to a polymorphic serializer, and shared by both SQL stores, for
/// two reasons. The shapes are the protocol's, not the CLR's: an attestor is discriminated by
/// <c>kind</c> and a blocked marker by <c>code</c>, each arm carrying exactly the fields that arm
/// makes meaningful, and a serializer configured to emit a .NET type discriminator would put a
/// different document in the column than the one the protocol's schema describes. And an unknown
/// discriminator must be an error a store reports, not a silently-null property: a row whose
/// attestation failed to read would be a row that says nobody agreed to a write somebody did agree
/// to.
/// </para>
/// </remarks>
internal static class DocketRowSerialization
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Attestation ─────────────────────────────────────────────────────────

    public static string? WriteAttestation(Attestation? attestation)
    {
        if (attestation is null) return null;

        var by = attestation.By switch
        {
            Attestor.Member m => new JsonObject
            {
                ["kind"] = "member",
                ["id"] = m.Id
            },
            Attestor.MemberViaRelay r => new JsonObject
            {
                ["kind"] = "member-via-relay",
                ["memberId"] = r.MemberId,
                ["relay"] = new JsonObject
                {
                    ["principal"] = r.Relay.Principal,
                    ["channelIdentity"] = r.Relay.ChannelIdentity,
                    ["messageId"] = r.Relay.MessageId
                }
            },
            Attestor.StandingOrder s => new JsonObject
            {
                ["kind"] = "standing-order",
                ["policyId"] = s.PolicyId,
                ["version"] = s.Version
            },
            _ => throw new InvalidOperationException(
                $"Unknown attestor kind '{attestation.By.Kind}'. The three kinds are closed; a " +
                "fourth cannot be persisted because no reader would know what it claims.")
        };

        return new JsonObject
        {
            ["by"] = by,
            ["at"] = Instant(attestation.At),
            ["entryId"] = attestation.EntryId.ToString()
        }.ToJsonString(s_options);
    }

    public static Attestation? ReadAttestation(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The stored attestation is not a JSON object.");
        var by = node["by"]?.AsObject()
            ?? throw new InvalidOperationException("The stored attestation names nobody.");

        var kind = (string?)by["kind"];
        Attestor attestor = kind switch
        {
            "member" => Attestor.Member.FromStorage(Required(by, "id")),
            "member-via-relay" => Attestor.MemberViaRelay.FromStorage(
                Required(by, "memberId"),
                ReadRelay(by["relay"]?.AsObject())),
            "standing-order" => Attestor.StandingOrder.FromStorage(
                Required(by, "policyId"), Required(by, "version")),
            _ => throw new InvalidOperationException(
                $"Unknown attestor kind '{kind}' on a stored attestation. A row whose attestation " +
                "cannot be read is a row that cannot say who agreed to the write.")
        };

        return new Attestation(
            attestor,
            ParseInstant(Required(node, "at")),
            Guid.Parse(Required(node, "entryId")));
    }

    private static AttestationRelay ReadRelay(JsonObject? relay)
    {
        if (relay is null)
        {
            throw new InvalidOperationException(
                "A member-via-relay attestation names the relay that carried the decision; this one " +
                "does not, and would read as though the person signed in directly.");
        }

        return new AttestationRelay(
            Required(relay, "principal"),
            Required(relay, "channelIdentity"),
            Required(relay, "messageId"));
    }

    // ── Blocked marker ──────────────────────────────────────────────────────

    public static string? WriteBlocked(BlockedMarker? blocked) => blocked switch
    {
        null => null,
        BlockedMarker.RequirementNotImplemented r => new JsonObject
        {
            ["code"] = "requirement-not-implemented",
            ["level"] = r.Level.ToString()
        }.ToJsonString(s_options),
        BlockedMarker.CoverageRefused c => new JsonObject
        {
            ["code"] = "coverage-refused",
            ["category"] = CategoryName(c.Category),
            ["toolName"] = c.ToolName
        }.ToJsonString(s_options),
        _ => throw new InvalidOperationException($"Unknown blocked code '{blocked.Code}'.")
    };

    public static BlockedMarker? ReadBlocked(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The stored blocked marker is not a JSON object.");
        var code = (string?)node["code"];
        return code switch
        {
            "requirement-not-implemented" => new BlockedMarker.RequirementNotImplemented(
                Enum.Parse<ReviewRequirement>(Required(node, "level"))),
            "coverage-refused" => new BlockedMarker.CoverageRefused(
                ParseCategory(Required(node, "category")), Required(node, "toolName")),
            _ => throw new InvalidOperationException(
                $"Unknown blocked code '{code}'. A row whose blocked marker cannot be read is a row " +
                "whose card cannot say why it refuses every decision.")
        };
    }

    private static string CategoryName(CoverageCategory category) => category switch
    {
        CoverageCategory.NoExecute => "no-execute",
        CoverageCategory.ProviderExecuted => "provider-executed",
        CoverageCategory.HostedMcp => "hosted-mcp",
        _ => throw new InvalidOperationException($"Unknown coverage category '{category}'.")
    };

    private static CoverageCategory ParseCategory(string name) => name switch
    {
        "no-execute" => CoverageCategory.NoExecute,
        "provider-executed" => CoverageCategory.ProviderExecuted,
        "hosted-mcp" => CoverageCategory.HostedMcp,
        _ => throw new InvalidOperationException($"Unknown coverage category '{name}'.")
    };

    // ── Decision record ─────────────────────────────────────────────────────

    public static string? WriteDecision(DecisionRecord? decision) =>
        decision is null
            ? null
            : new JsonObject
            {
                ["kind"] = decision.Kind == DecisionKind.Approve ? "approve" : "reject",
                ["reason"] = decision.Reason,
                ["at"] = Instant(decision.At)
            }.ToJsonString(s_options);

    public static DecisionRecord? ReadDecision(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The stored decision is not a JSON object.");
        var kind = (string?)node["kind"] switch
        {
            "approve" => DecisionKind.Approve,
            "reject" => DecisionKind.Reject,
            var other => throw new InvalidOperationException($"Unknown decision kind '{other}'.")
        };
        return new DecisionRecord(kind, (string?)node["reason"], ParseInstant(Required(node, "at")));
    }

    // ── Preserved amendments ────────────────────────────────────────────────

    public static string? WritePreservedAmendments(PreservedAmendments? preserved)
    {
        if (preserved is null) return null;

        var amendments = JsonNode.Parse(JsonSerializer.Serialize(preserved.Amendments, s_options));
        return new JsonObject
        {
            ["amendments"] = amendments,
            ["at"] = Instant(preserved.At),
            ["by"] = preserved.By
        }.ToJsonString(s_options);
    }

    public static PreservedAmendments? ReadPreservedAmendments(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The stored preserved amendments are not a JSON object.");
        var map = ReadAmendments(node["amendments"]?.ToJsonString())
            ?? new Dictionary<string, object?>();
        return new PreservedAmendments(map, ParseInstant(Required(node, "at")), Required(node, "by"));
    }

    // ── Amendment maps ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads an amendment map, keeping a <c>null</c> value distinct from an absent key: a null means
    /// the reviewer cleared the field, an absent key means they left it untouched, and an
    /// implementation never conflates the two.
    /// </summary>
    /// <remarks>
    /// Values come back as the CLR values they were written as, not as raw JSON elements: a
    /// resubmission prefills these into the new proposal's fields, where a host risk scorer that
    /// pattern-matches on the value's type would otherwise see a type it does not recognise for
    /// every corrected field.
    /// </remarks>
    public static IReadOnlyDictionary<string, object?>? ReadAmendments(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, s_options);
        if (raw is null) return null;

        var result = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
        foreach (var (k, v) in raw)
            result[k] = AffidavitFieldValues.Typed(v, kind: null);

        return result;
    }

    public static string? WriteAmendments(IReadOnlyDictionary<string, object?>? amendments) =>
        amendments is null ? null : JsonSerializer.Serialize(amendments, s_options);

    // ── Instants ────────────────────────────────────────────────────────────

    private static string Instant(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Required(JsonObject node, string name) =>
        (string?)node[name]
        ?? throw new InvalidOperationException($"A stored Docket fact is missing its '{name}'.");
}
