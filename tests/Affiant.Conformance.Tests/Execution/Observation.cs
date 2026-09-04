using System.Text.Json.Nodes;
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
/// Every projection here is a <b>reading of the record</b>, never a re-derivation of a rule. Where
/// the record has no such property the projection says so, and the two ways of saying so are
/// different on purpose:
/// </para>
/// <list type="bullet">
/// <item><b>A stated <c>null</c>.</b> Used where the release genuinely holds the fact and the fact
/// is "nothing": there is no attestation on any row, so <c>attestation</c> reads <c>null</c> and a
/// fixture asserting <c>attestation: null</c> is correct to pass. The same for <c>blocked</c>,
/// <c>decision</c>, <c>execution</c>, <c>preservedAmendments</c> and <c>amendedAffidavit</c>.</item>
/// <item><b>An absent key.</b> Used where the property does not exist at all and no reading of the
/// record could answer it: <c>populatedConfidence</c>, <c>channel</c>, <c>executionDetail</c>,
/// <c>bindingKind</c>, <c>canonicalDiffersFromProposal</c>, the card's <c>protocolVersion</c>. A
/// fixture stating one of those fails, with <c>(absent)</c> on the actual side.</item>
/// </list>
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
    public static JsonObject Entry(DocketEntry entry, EntryFacts facts)
    {
        var row = new JsonObject
        {
            ["status"] = Status(entry.Status),
            ["toolName"] = entry.OperationType,
            ["tenantId"] = entry.TenantId,
            ["conversationId"] = entry.SessionId,
            ["expiresAtOffsetMs"] = (long)Math.Round((entry.ExpiresAt - entry.CreatedAt).TotalMilliseconds),

            // Held as facts by this release, and the fact is "nothing".
            ["attestation"] = null,
            ["blocked"] = null,
            ["decision"] = null,
            ["execution"] = null,
            ["preservedAmendments"] = null,
            ["amendedAffidavit"] = null,

            ["amendments"] = entry.Amendments is null ? null : Values.ToJson(entry.Amendments),
            ["lineage"] = new JsonObject
            {
                ["supersedes"] = facts.Supersedes?.ToString(),
                ["supersededBy"] = entry.ResubmittedTo?.ToString(),
            },
            ["affidavit"] = Affidavit(entry.Envelope),
        };

        // The requirement is not on the row in this release. What the framework reported when it
        // filed is the nearest true reading, and it is a reading of an answer the framework gave,
        // not a guess: an auto-approval means the chain returned StandingOrder, a referral means
        // ReferralRequired, and a card means ReviewerConfirmation — or MultiParty, which this
        // release routes down the same branch and so cannot be told apart. That indistinguishability
        // is AZ-4's defect, and it surfaces as a diff on any fixture that states MultiParty.
        if (facts.Requirement is not null)
        {
            row["requirement"] = facts.Requirement;
        }

        return row;
    }

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

            // `populatedConfidence` has no property on this record and no reading of the record can
            // answer it: computing one here would be the driver implementing AF-2 on the framework's
            // behalf, which is the rule the fixture is about.
            //
            // `emptyFieldCount`, by contrast, is a count over the field list the record already
            // carries — the same kind of projection `source` and `bound` are — so it is read rather
            // than left absent.
            ["emptyFieldCount"] = affidavit.Fields.Count(f => f.Provenance.Current.Source == ProvenanceSource.Empty),
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

        return new JsonObject
        {
            ["name"] = field.Name,
            ["value"] = Values.ToJson(field.Value),
            ["previousValue"] = Values.ToJson(field.PreviousValue),
            ["kind"] = field.Kind,
            ["isMandatory"] = field.IsMandatory,
            ["source"] = field.Provenance.Current.Source.ToString(),
            ["confidence"] = field.Provenance.Current.Confidence,

            // A tag in this release carries a source, a confidence, an evidence string and a
            // conversation turn — and nothing that points at a record an auditor could re-fetch. No
            // tag is ever bound, so `bound` reads false everywhere and `bindingKind` is absent (PV-2).
            ["bound"] = false,
            ["priorSources"] = prior,
        };
    }

    /// <summary>The Evidence Card, as the fixture's <c>card</c> matcher reads it.</summary>
    public static JsonObject Card(EvidenceCardRequest card)
    {
        var affidavit = Affidavit(card.Affidavit);
        var fields = new JsonArray();
        foreach (var field in card.Affidavit.Fields)
        {
            fields.Add(new JsonObject
            {
                ["name"] = field.Name,
                ["kind"] = field.Kind,
                ["value"] = Values.ToJson(field.Value),
                ["allowedValues"] = field.AllowedValues is null ? null : new JsonArray(field.AllowedValues.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["pattern"] = field.Pattern,
                ["isMandatory"] = field.IsMandatory,
            });
        }

        return new JsonObject
        {
            ["requiresConfirmation"] = card.Affidavit.RequiresConfirmation,
            ["warnings"] = new JsonArray(card.Affidavit.Warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["priorAmendments"] = card.PriorAmendments is null ? null : Values.ToJson(card.PriorAmendments),
            ["blocked"] = null,
            ["aggregateConfidence"] = affidavit["aggregateConfidence"]?.DeepClone(),
            ["emptyFieldCount"] = affidavit["emptyFieldCount"]?.DeepClone(),
            ["fields"] = fields,
        };
    }

    /// <summary>
    /// The three facts that hold for every card the gate ever produces, checked on every filing
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

        // SR-4: the card carries the protocol version it was built under. `EvidenceCardRequest` has
        // four properties — DocketId, Affidavit, RequiredBy, PriorAmendments — and no version among
        // them, so this invariant cannot hold on any filing in this release.
        into.Add(Mismatch.Said(
            "card.protocolVersion",
            "the protocol version the card was built under (SR-4)",
            "(absent) — EvidenceCardRequest carries no version"));

        // AF-2/SR-1: the card's confidence numbers are the record's. They are, here — the card is
        // built from the row's own Envelope — so this one holds and is checked rather than assumed.
        if (!ReferenceEquals(card.Affidavit, entry.Envelope) && card.Affidavit != entry.Envelope)
        {
            into.Add(Mismatch.Said(
                "card.affidavit",
                "the row's own Affidavit",
                "a different Affidavit from the row's"));
        }
    }

    /// <summary>What the driver knows about a row beyond what the row itself holds.</summary>
    internal sealed record EntryFacts(string? Requirement, Guid? Supersedes);
}
