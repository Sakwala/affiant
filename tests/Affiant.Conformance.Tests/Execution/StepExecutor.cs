using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Conformance.Tests.Model;
using Affiant.Conformance.Tests.Ports;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Conformance.Tests.Execution;

/// <summary>
/// Binds each of the eight step kinds to one entry point on the shipped packages
/// (<c>DRIVER.md</c> §3), and records what happened.
/// </summary>
/// <remarks>
/// <para><b>The bindings.</b></para>
/// <list type="table">
/// <listheader><term>Step</term><description>Entry point in <c>1.0.0-beta.1</c></description></listheader>
/// <item><term><c>file</c></term><description><c>SchemaDrivenAffidavitProjection.Project</c> over a fabric carrying the step's prepared fields, then <c>ReviewGate.FileForReviewAsync</c>.</description></item>
/// <item><term><c>wrap-execute</c></term><description>The same gate entry point, with the model's arguments tagged the way <c>ToolArgumentCaptureFilter</c> tags them (<c>ProvenanceTag.FromTool</c>, confidence 0.9, source <c>Conversation</c>). The wrapped-tool surface itself lives in the three adapter packages and reaches the gate only through a JSON-serialized <c>WriteProposal</c> and a host's <c>IReviewContextProvider</c>; the gate it reaches is this one. <c>IWriteExecutor</c> is armed as GT-6's tripwire throughout.</description></item>
/// <item><term><c>decide</c></term><description><c>ReviewGate.HandleDecisionAsync</c>.</description></item>
/// <item><term><c>resubmit</c></term><description><c>ReviewGate.ResubmitAsync</c>.</description></item>
/// <item><term><c>get</c></term><description><c>IDocketStore.GetDocketEntryAsync</c>.</description></item>
/// <item><term><c>expireDue</c></term><description><c>DocketExpiryService.ExpireOverdueAsync</c>. It takes no limit, no scope and no cursor, and reports nothing: the sweep in this release loads every pending entry on every instance (DK-3).</description></item>
/// <item><term><c>rehydrate</c></term><description><c>ReviewGate.RebroadcastPendingCardsAsync</c>. It re-broadcasts a session's pending cards and returns nothing: no page, no cursor, no <c>more</c>, no order the caller can read (DK-5).</description></item>
/// <item><term><c>markExecuted</c></term><description><b>No counterpart.</b> Nothing in the release records what became of the write, so the step cannot be run at all.</description></item>
/// </list>
/// </remarks>
internal sealed class StepExecutor(GateHarness harness, GivenSpec given)
{
    private readonly Dictionary<string, Guid> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _requirements = [];
    private Guid? _lastFiled;

    /// <summary>What one step did.</summary>
    internal sealed record StepResult(Refusal? Refusal)
    {
        public Guid? EntryId { get; init; }

        public EvidenceCardRequest? Card { get; init; }

        public JsonObject? Expired { get; init; }

        public JsonObject? Page { get; init; }

        public bool? Found { get; init; }

        /// <summary>Set when the step kind has no counterpart in this release: the fixture fails, and this says why.</summary>
        public string? NotImplemented { get; init; }
    }

    /// <summary>The row a clause is about: the step's own, or the last one filed.</summary>
    public Guid? Current => _lastFiled;

    /// <summary>The requirement the framework reported when a row was filed, for the row observation.</summary>
    public string? RequirementOf(Guid entryId) => _requirements.GetValueOrDefault(entryId);

    /// <summary>Runs one act.</summary>
    public async Task<StepResult> RunAsync(StepSpec step, CancellationToken ct)
    {
        if (step.At is { } at)
        {
            harness.Clock = at;
        }

        var conversationId = step.ConversationId ?? given.Ctx.ConversationId;

        try
        {
            return step.Kind switch
            {
                "file" or "wrap-execute" => await FileAsync(step, conversationId, ct),
                "decide" => await DecideAsync(step, conversationId, ct),
                "resubmit" => await ResubmitAsync(step, conversationId, ct),
                "get" => await GetAsync(step, ct),
                "expireDue" => await ExpireDueAsync(ct),
                "rehydrate" => await RehydrateAsync(step, conversationId, ct),
                // The step cannot be run, but the row it would have acted on is still the row every
                // other clause of the fixture is about, so it is carried forward rather than lost.
                "markExecuted" => new StepResult(null)
                {
                    EntryId = Keep(step),
                    NotImplemented =
                        "markExecuted: 1.0.0-beta.1 records no execution outcome. A Docket row has no execution " +
                        "state and IDocketStore has no way to report one, so an approved-but-failed write is " +
                        "indistinguishable from an approved-and-committed one (DK-1, AZ-5, AZ-7).",
                },
                _ => new StepResult(null) { NotImplemented = $"step kind \"{step.Kind}\" is not bound." },
            };
        }
        catch (Exception exception) when (RefusalCodes.FromException(exception) is not null)
        {
            // A refusal does not lose the row: the fixture's other clauses are still about it.
            return new StepResult(RefusalCodes.FromException(exception)) { EntryId = Target(step) };
        }
    }

    private async Task<StepResult> FileAsync(StepSpec step, string conversationId, CancellationToken ct)
    {
        var services = harness.Conversation(conversationId);
        var fabric = services.GetRequiredService<ContextFabric>();
        var tenantId = step.TenantId ?? given.Ctx.TenantId;
        var principal = step.PrincipalStated ? step.Principal : given.Ctx.Principal;

        var isWrap = step.Kind == "wrap-execute";
        var toolName = isWrap ? step.Tool!.Name : step.ToolName!;
        var strategy = isWrap ? FixtureStrategy.ForTool(step.Tool!) : FixtureStrategy.ForFile(step);
        var operationType = (isWrap ? step.Tool!.EntityId : step.Operation!.EntityId) is null ? "WriteCreate" : "WriteUpdate";

        // The proposed values, as the entity the projection reads. ContextFabric keys entities by
        // EntityId and SchemaDrivenAffidavitProjection looks one up by the strategy's EntityName, so
        // that is the key a host has to use.
        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        if (isWrap)
        {
            // What ToolArgumentCaptureFilter does to a model's arguments, verbatim: one
            // Conversation tag at 0.9 per named argument, evidence naming the tool.
            foreach (var (name, value) in step.Args!)
            {
                fabric.SetFieldChain(name, ProvenanceChain.From(ProvenanceTag.FromTool(toolName, 0.9f)));
                if (Values.ToClr(value) is { } clr)
                {
                    values[name] = clr;
                }
            }
        }
        else
        {
            foreach (var field in step.PreparedFields ?? [])
            {
                var tag = field.Provenance is { } p
                    ? new ProvenanceTag(Enum.Parse<ProvenanceSource>(p.Source), (float)p.Confidence, p.Note, null)
                    : ProvenanceTag.Empty;
                fabric.SetFieldChain(field.Name, ProvenanceChain.From(tag));
                if (Values.ToClr(field.Value) is { } clr)
                {
                    values[field.Name] = clr;
                }
            }
        }

        if (values.Count > 0)
        {
            fabric.Upsert(new EntityRef(strategy.EntityName, strategy.EntityName, strategy.EntityName, values));
        }

        // GT-1 step 3: the host's inference reports its scripted fields, and the framework's own
        // merge decides what wins. Running it after the prepared tags is what makes a 0.6 inferred
        // tag lose to a 0.9 conversation tag and land in the chain's prior, rather than the driver
        // deciding that itself.
        if (harness.Spec.Inference.Count > 0)
        {
            var runner = services.GetRequiredService<TaskInferenceRunner>();
            var arguments = (step.Args ?? new Dictionary<string, JsonNode?>())
                .ToDictionary(kv => kv.Key, kv => Values.ToClr(kv.Value), StringComparer.Ordinal);
            await runner.RunAsync(strategy, [], toolName, arguments!, ct);
        }

        var projection = new SchemaDrivenAffidavitProjection(
            strategy,
            services.GetServices<IFieldResolver>(),
            [],
            NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            services.GetRequiredService<IObservabilityEventStream<AffidavitEmittedEvent>>());

        var affidavit = projection.Project(fabric, operationType, []);
        var actor = principal?.Id ?? "(unresolved)";
        var entryId = DerivedEntryId(tenantId, conversationId, toolName, step);

        var proposal = new WriteProposal(toolName, harness.Clock, affidavit);
        var context = new ReviewContext(
            SessionId: conversationId,
            TenantId: tenantId,
            UserId: actor,
            ReviewerUserId: actor,
            Affidavit: affidavit,
            EntryId: entryId);

        var mark = harness.Transport.Mark();
        var filing = await harness.GateFor(conversationId).FileForReviewAsync(proposal, context, ct);

        // The requirement is not written to the row in this release; what the framework reported is
        // the nearest true reading of the chain's answer. See Observation.Entry.
        _requirements[entryId] = filing switch
        {
            ReviewFilingResult.Decided { Outcome: ReviewOutcome.Approved } => "StandingOrder",
            ReviewFilingResult.Decided { Outcome: ReviewOutcome.Referral } => "ReferralRequired",
            _ => "ReviewerConfirmation",
        };

        _lastFiled = entryId;
        Remember(step, entryId);

        var card = harness.Transport.Since(mark)
            .Where(b => b.Event == TransportEvent.EvidenceCardRequest)
            .Select(b => (EvidenceCardRequest)b.Payload)
            .LastOrDefault();

        return new StepResult(null) { EntryId = entryId, Card = card };
    }

    private async Task<StepResult> DecideAsync(StepSpec step, string conversationId, CancellationToken ct)
    {
        var entryId = Target(step);
        if (entryId is null || entryId == Guid.Empty)
        {
            return new StepResult(new Refusal(RefusalCodes.EntryNotFound, "No entry has been filed for this step to act on."));
        }

        var decision = step.Decision!;
        var amendments = decision.Amendments?.ToDictionary(kv => kv.Key, kv => Values.ToClr(kv.Value), StringComparer.Ordinal);

        var (outcome, _) = await harness.GateFor(conversationId).HandleDecisionAsync(
            entryId.Value,
            decision.Kind == "approve" ? ApprovalDecision.Approved : ApprovalDecision.Rejected,
            amendments is { Count: > 0 } ? amendments! : null,
            ct);

        var refusal = outcome switch
        {
            null => new Refusal(RefusalCodes.EntryNotFound, $"DocketEntry {entryId} was not found."),

            // The gate reports Expired both for a row whose deadline had lapsed and for a row that
            // was no longer pending; they are the same return value, so the store decides which
            // refusal it was.
            ReviewOutcome.Expired => await ReadExpiryRefusalAsync(entryId.Value, ct),
            _ => null,
        };

        _lastFiled = entryId;
        Remember(step, entryId.Value);
        return new StepResult(refusal) { EntryId = entryId };
    }

    private async Task<Refusal> ReadExpiryRefusalAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await harness.Store.GetDocketEntryAsync(entryId, ct);
        return entry is null
            ? new Refusal(RefusalCodes.EntryNotFound, $"DocketEntry {entryId} was not found.")
            : entry.Status is ReviewStatus.Approved or ReviewStatus.Rejected
                ? new Refusal(RefusalCodes.DecisionNotPending, $"DocketEntry {entryId} is {entry.Status}.")
                : new Refusal(RefusalCodes.DecisionExpired, $"DocketEntry {entryId} passed its deadline before the decision arrived.");
    }

    private async Task<StepResult> ResubmitAsync(StepSpec step, string conversationId, CancellationToken ct)
    {
        var entryId = Target(step);
        if (entryId is null || entryId == Guid.Empty)
        {
            return new StepResult(new Refusal(RefusalCodes.EntryNotFound, "No entry has been filed for this step to act on."));
        }

        var filing = await harness.GateFor(conversationId).ResubmitAsync(entryId.Value, ct);
        var newId = filing switch
        {
            ReviewFilingResult.RequiresReview r => r.EntryId,
            ReviewFilingResult.Decided d => d.Outcome.DocketId,
            _ => entryId.Value,
        };

        _requirements[newId] = filing is ReviewFilingResult.Decided { Outcome: ReviewOutcome.Approved }
            ? "StandingOrder"
            : "ReviewerConfirmation";
        _lastFiled = newId;
        Remember(step, newId);
        return new StepResult(null) { EntryId = newId, Card = harness.Transport.CardFor(newId) };
    }

    private async Task<StepResult> GetAsync(StepSpec step, CancellationToken ct)
    {
        var entryId = Target(step);
        var entry = entryId is null ? null : await harness.Store.GetDocketEntryAsync(entryId.Value, ct);
        return new StepResult(null) { EntryId = entryId, Found = entry is not null };
    }

    private async Task<StepResult> ExpireDueAsync(CancellationToken ct)
    {
        var before = await harness.Store.ListAllPendingAsync(ct);
        await harness.Sweep.ExpireOverdueAsync(ct);
        var after = await harness.Store.ListAllPendingAsync(ct);

        // The sweep reports nothing, so the count is read from what it changed. `more` has no
        // counterpart at all: a sweep that took every entry it found cannot say whether there were
        // others it did not take.
        return new StepResult(null)
        {
            Expired = new JsonObject { ["count"] = before.Count - after.Count },
        };
    }

    private async Task<StepResult> RehydrateAsync(StepSpec step, string conversationId, CancellationToken ct)
    {
        var sessionId = step.Scope?.ConversationId ?? conversationId;
        var mark = harness.Transport.Mark();
        await harness.GateFor(conversationId).RebroadcastPendingCardsAsync(sessionId, ct);
        var cards = harness.Transport.Since(mark)
            .Count(b => b.Event == TransportEvent.EvidenceCardRequest);

        // What was pushed is all a caller can observe. There is no page, no cursor and no status
        // list: the surface re-broadcasts and returns void.
        return new StepResult(null)
        {
            Page = new JsonObject { ["count"] = cards },
            NotImplemented = cards <= step.Page!.Limit && step.Page.Cursor is null
                ? null
                : "rehydrate: RebroadcastPendingCardsAsync takes no page and no cursor and returns nothing, " +
                  "so a page limit cannot be honoured and the order DK-5 requires cannot be read.",
        };
    }

    private Guid? Keep(StepSpec step)
    {
        var entryId = Target(step);
        if (entryId is { } id)
        {
            _lastFiled = id;
            Remember(step, id);
        }

        return entryId;
    }

    private Guid? Target(StepSpec step) =>
        step.Entry is { } label ? _labels.GetValueOrDefault(label) : _lastFiled;

    private void Remember(StepSpec step, Guid entryId)
    {
        if (step.As is { } label)
        {
            _labels[label] = entryId;
        }
    }

    /// <summary>
    /// The entry id a proposal derives to. An id derived from the proposal is what makes a re-file
    /// of the same proposal a replay rather than a second row (GT-4, DK-1); the framework leaves the
    /// choice to the host (<c>ReviewContext.EntryId</c>), so the driver makes it here and makes it
    /// deterministic.
    /// </summary>
    /// <remarks>
    /// It is derived from the <b>proposal the step states</b> — the tool, the entity it names and the
    /// values passed — and not from the projected Affidavit. That distinction is load-bearing here:
    /// every Affidavit this release produces is create-shaped with a null entity id (AF-3), so three
    /// updates to three different invoices project to three identical Affidavits. A host deriving its
    /// id from the projection would file one row for all three; deriving it from the proposal keeps
    /// the three apart, and leaves AF-3 to be reported where it belongs rather than compounded into
    /// an id collision.
    /// </remarks>
    private static Guid DerivedEntryId(string tenantId, string conversationId, string toolName, StepSpec step)
    {
        var seed = new StringBuilder()
            .Append(tenantId).Append('|')
            .Append(conversationId).Append('|')
            .Append(toolName).Append('|')
            .Append(step.Tool?.EntityType ?? step.Operation?.EntityType).Append('|')
            .Append(step.Tool?.EntityId ?? step.Operation?.EntityId ?? "(create)");

        foreach (var (name, value) in (step.Args ?? new Dictionary<string, JsonNode?>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            seed.Append('|').Append(name).Append('=').Append(value?.ToJsonString() ?? "null");
        }

        foreach (var field in step.PreparedFields ?? [])
        {
            seed.Append('|').Append(field.Name).Append('=').Append(field.Value?.ToJsonString() ?? "null");
        }

        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString())).AsSpan(0, 16));
    }
}
