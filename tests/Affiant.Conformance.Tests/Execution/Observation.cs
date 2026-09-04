using System.Text.Json.Nodes;
using Affiant.Abstractions;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Conformance.Tests.Matching;
using Affiant.Conformance.Tests.Ports;

namespace Affiant.Conformance.Tests.Execution;

/// <summary>
/// Projects what the framework did into the shapes a fixture states, so the matcher compares JSON
/// with JSON and a mismatch reports the path the fixture wrote.
/// </summary>
/// <remarks>
/// <para>
/// Every projection here is a <b>reading of the record</b>, never a re-derivation of a rule: the
/// driver reports what the row and the card hold, and where the record holds nothing the projection
/// says <c>null</c> rather than computing what the rule would have wanted. A driver that computed a
/// rule's answer on the implementation's behalf would pass the fixture that rule is about.
/// </para>
/// </remarks>
internal static class Observation
{
    /// <summary>How a stored <see cref="ReviewStatus"/> reads as the status a fixture states.</summary>
    /// <remarks>
    /// <c>Deferred</c> has no counterpart in the four the format defines — it is this release's
    /// referral state — so it is reported verbatim and a fixture that expected one of the four sees
    /// it in the diff rather than being silently given the nearest neighbour.
    /// </remarks>
    public static string Status(ReviewStatus status) => status switch
    {
        ReviewStatus.Pending => "pending",
        ReviewStatus.Approved => "approved",
        ReviewStatus.Rejected => "rejected",
        ReviewStatus.Expired => "expired",
        _ => "deferred",
    };

    /// <summary>The Docket row, as the fixture's <c>entry</c> matcher reads it.</summary>
    /// <remarks>
    /// <c>status</c> is the status the row <b>reads</b>, not the one it stores: a row past its
    /// deadline reads <c>expired</c> whether or not a sweep has run (DK-1), so the fixture's clock
    /// is applied here. Every other clause is a plain read of the record — <c>requirement</c>
    /// included, which the row now carries rather than the driver inferring it from what happened
    /// afterwards.
    /// </remarks>
    public static JsonObject Entry(DocketEntry entry, EntryFacts facts)
    {
        var reads = entry.Status == ReviewStatus.Pending && facts.Now >= entry.ExpiresAt
            ? ReviewStatus.Expired
            : entry.Status;

        var row = new JsonObject
        {
            ["status"] = Status(reads),
            ["toolName"] = entry.ToolName,
            ["tenantId"] = entry.TenantId,
            ["conversationId"] = entry.SessionId,
            ["channel"] = entry.Channel,
            ["expiresAtOffsetMs"] = (long)Math.Round((entry.ExpiresAt - entry.CreatedAt).TotalMilliseconds),
            ["execution"] = entry.Execution is { } e ? Execution(e) : null,
            ["executionDetail"] = entry.ExecutionDetail,
            ["attestation"] = Attestation(entry.Attestation),
            ["blocked"] = Blocked(entry.Blocked),
            ["decision"] = entry.Decision is { } d
                ? new JsonObject { ["kind"] = d.Kind == DecisionKind.Approve ? "approve" : "reject", ["reason"] = d.Reason }
                : null,
            ["preservedAmendments"] = entry.PreservedAmendments is { } p ? Values.ToJson(p.Amendments) : null,
            ["amendedAffidavit"] = entry.AmendedAffidavit is { } a ? Affidavit(a) : null,
            ["amendments"] = entry.Amendments is null ? null : Values.ToJson(entry.Amendments),
            ["lineage"] = new JsonObject
            {
                ["supersedes"] = entry.Supersedes?.ToString(),
                ["supersededBy"] = entry.ResubmittedTo?.ToString(),
            },
            ["requirement"] = entry.Requirement.ToString(),
            ["affidavit"] = Affidavit(entry.Envelope),
            ["canonicalDiffersFromProposal"] = entry.AmendedAffidavit is { } amended
                && Affiant.Core.Serialization.CanonicalSerializer.CanonicalHash(amended)
                    != Affiant.Core.Serialization.CanonicalSerializer.CanonicalHash(entry.Envelope),
        };

        return row;
    }

    /// <summary>An execution outcome, as the fixture spells it.</summary>
    public static string Execution(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Executed => "executed",
        ExecutionOutcome.Failed => "failed",
        _ => "unexecuted",
    };

    /// <summary>
    /// The attestation record's <b>attestor</b> — the shape <c>RUNNER.md</c> §4.1 defines — or
    /// <c>null</c> for none.
    /// </summary>
    public static JsonObject? Attestation(Attestation? attestation) => attestation?.By switch
    {
        null => null,
        Attestor.Member m => new JsonObject { ["kind"] = "member", ["id"] = m.Id },
        Attestor.MemberViaRelay r => new JsonObject
        {
            ["kind"] = "member-via-relay",
            ["memberId"] = r.MemberId,
            ["relay"] = new JsonObject
            {
                ["principal"] = r.Relay.Principal,
                ["channelIdentity"] = r.Relay.ChannelIdentity,
                ["messageId"] = r.Relay.MessageId,
            },
        },
        Attestor.StandingOrder s => new JsonObject
        {
            ["kind"] = "standing-order",
            ["policyId"] = s.PolicyId,
            ["version"] = s.Version,
        },
        _ => new JsonObject { ["kind"] = "(unknown)" },
    };

    /// <summary>The blocked marker, as the fixture states it.</summary>
    public static JsonObject? Blocked(BlockedMarker? marker) => marker switch
    {
        null => null,
        BlockedMarker.RequirementNotImplemented r => new JsonObject
        {
            ["code"] = r.Code,
            ["level"] = r.Level.ToString(),
        },
        BlockedMarker.CoverageRefused c => new JsonObject
        {
            ["code"] = c.Code,
            ["category"] = c.Category switch
            {
                CoverageCategory.NoExecute => "no-execute",
                CoverageCategory.ProviderExecuted => "provider-executed",
                _ => "hosted-mcp",
            },
            ["tool"] = c.ToolName,
        },
        _ => new JsonObject { ["code"] = marker.Code },
    };

    /// <summary>The Affidavit, as the fixture's <c>affidavit</c> matcher reads it.</summary>
    public static JsonObject Affidavit(Affidavit affidavit)
    {
        var fields = new JsonArray();
        foreach (var field in affidavit.Fields)
        {
            fields.Add(Field(field));
        }

        return new JsonObject
        {
            ["operationType"] = affidavit.OperationType switch
            {
                "WriteCreate" or "create" => "create",
                "WriteUpdate" or "update" => "update",
                _ => affidavit.OperationType,
            },
            ["entityType"] = affidavit.EntityType,
            ["entityId"] = affidavit.EntityId,
            ["aggregateConfidence"] = affidavit.AggregateConfidence,
            ["populatedConfidence"] = affidavit.PopulatedConfidence,
            ["emptyFieldCount"] = affidavit.EmptyFieldCount,
            ["fields"] = fields,
        };
    }

    private static JsonObject Field(AffidavitField field)
    {
        var prior = new JsonArray();
        foreach (var tag in field.Provenance.Prior)
        {
            prior.Add(tag.Source.ToString());
        }

        var current = field.Provenance.Current;
        return new JsonObject
        {
            ["name"] = field.Name,
            ["value"] = Values.ToJson(field.Value),
            ["previousValue"] = Values.ToJson(field.PreviousValue),
            ["kind"] = field.Kind,
            ["isMandatory"] = field.IsMandatory,
            ["source"] = current.Source.ToString(),
            ["confidence"] = current.Confidence,

            // `bound` is whether the tag in force points at something an auditor can re-check
            // (PV-2, PV-4); `bindingKind` is which kind it points with.
            ["bound"] = current.Binding is not null,
            ["bindingKind"] = current.Binding?.Kind,
            ["priorSources"] = prior,
        };
    }

    /// <summary>The Evidence Card, as the fixture's <c>card</c> matcher reads it.</summary>
    public static JsonObject Card(EvidenceCardRequest card)
    {
        var fields = new JsonArray();
        foreach (var field in card.Affidavit.Fields)
        {
            fields.Add(new JsonObject
            {
                ["name"] = field.Name,
                ["kind"] = field.Kind,
                ["value"] = Values.ToJson(field.Value),
                ["isMandatory"] = field.IsMandatory,
            });
        }

        var presentation = new JsonArray();
        foreach (var hint in card.Presentation ?? [])
        {
            presentation.Add(new JsonObject
            {
                ["name"] = hint.Name,
                ["kind"] = hint.Kind,
                ["allowedValues"] = hint.AllowedValues is null
                    ? null
                    : new JsonArray(hint.AllowedValues.Select(Values.ToJson).ToArray()),
                ["pattern"] = hint.Pattern,
            });
        }

        return new JsonObject
        {
            ["requiresConfirmation"] = card.RequiresConfirmation,
            ["warnings"] = new JsonArray((card.Warnings ?? []).Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["priorAmendments"] = card.PriorAmendments is null ? null : Values.ToJson(card.PriorAmendments),
            ["blocked"] = Blocked(card.Blocked),
            ["protocolVersion"] = card.ProtocolVersion,
            ["aggregateConfidence"] = card.Affidavit.AggregateConfidence,
            ["populatedConfidence"] = card.PopulatedConfidence,
            ["emptyFieldCount"] = card.EmptyFieldCount,
            ["fields"] = fields,
            ["presentation"] = presentation,
        };
    }

    /// <summary>
    /// The card facts that hold for every card the gate ever produces, checked on every filing
    /// whether or not the fixture mentions them (<c>RUNNER.md</c> §4.2).
    /// </summary>
    /// <remarks>
    /// A driver that only checked what a fixture states would pass a card that disagreed with its
    /// own row on every one of the 56.
    /// </remarks>
    public static void CardInvariants(DocketEntry entry, EvidenceCardRequest? card, List<Mismatch> into)
    {
        if (card is null)
        {
            into.Add(Mismatch.Said("card", "a card for the row that was filed", "no card was broadcast"));
            return;
        }

        if (card.DocketId != entry.EntryId)
        {
            into.Add(Mismatch.Said("card.docketId", entry.EntryId.ToString(), card.DocketId.ToString()));
        }

        if (card.RequiredBy != entry.ExpiresAt)
        {
            into.Add(Mismatch.Said("card.requiredBy", entry.ExpiresAt.ToString("O"), card.RequiredBy.ToString("O")));
        }

        // SR-4: the card carries the row's own protocol version.
        if (card.ProtocolVersion != entry.ProtocolVersion)
        {
            into.Add(Mismatch.Said("card.protocolVersion", entry.ProtocolVersion, card.ProtocolVersion));
        }

        // AF-2/SR-1: the card's three numbers are the record's — the state an approval accepted
        // where there is one, the proposal otherwise.
        var record = entry.AmendedAffidavit ?? entry.Envelope;
        if (card.Affidavit != record)
        {
            into.Add(Mismatch.Said("card.affidavit", "the row's own Affidavit", "a different Affidavit from the row's"));
        }

        if (card.PopulatedConfidence != record.PopulatedConfidence)
        {
            into.Add(Mismatch.Said(
                "card.populatedConfidence",
                record.PopulatedConfidence?.ToString() ?? "null",
                card.PopulatedConfidence?.ToString() ?? "null"));
        }

        if (card.EmptyFieldCount != record.EmptyFieldCount)
        {
            into.Add(Mismatch.Said(
                "card.emptyFieldCount",
                record.EmptyFieldCount.ToString(),
                card.EmptyFieldCount.ToString()));
        }

        // AZ-4/CV-4: a blocked row says so on the card, and never asks for a confirmation no
        // decision path will accept.
        if (entry.Blocked is not null && card.Blocked is null)
        {
            into.Add(Mismatch.Said("card.blocked", entry.Blocked.Code, "(absent) — the card does not say the row is blocked"));
        }

        if (entry.Blocked is not null && card.RequiresConfirmation)
        {
            into.Add(Mismatch.Said("card.requiresConfirmation", "false on a blocked row", "true"));
        }
    }

    /// <summary>
    /// What the driver knows beyond what the row holds: the instant the fixture has reached, which
    /// is what makes a row past its deadline read <c>expired</c> (DK-1).
    /// </summary>
    internal sealed record EntryFacts(DateTimeOffset Now);
}
