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
/// <listheader><term>Step</term><description>Entry point</description></listheader>
/// <item><term><c>file</c></term><description><c>SchemaDrivenAffidavitProjection.Project</c> over a fabric carrying the step's prepared fields, then <c>ReviewGate.FileForReviewAsync</c>.</description></item>
/// <item><term><c>wrap-execute</c></term><description>The same gate entry point, with the model's arguments tagged the way <c>ToolArgumentCaptureFilter</c> tags them. The wrapped-tool surface itself lives in the three adapter packages and reaches the gate only through a JSON-serialized <c>WriteProposal</c> and a host's <c>IReviewContextProvider</c>; the gate it reaches is this one. <c>IWriteExecutor</c> is armed as GT-6's tripwire throughout.</description></item>
/// <item><term><c>decide</c></term><description><c>ReviewGate.HandleDecisionAsync</c>, with the step's principal, tenant, conversation and channel in a <c>DecisionContext</c> (AZ-2).</description></item>
/// <item><term><c>resubmit</c></term><description><c>ReviewGate.ResubmitAsync</c>.</description></item>
/// <item><term><c>get</c></term><description><c>IDocketStore.GetDocketEntryAsync</c>.</description></item>
/// <item><term><c>expireDue</c></term><description><c>IDocketStore.ExpireDueAsync(now, scope, limit)</c> — the host-scheduled, paged sweep (DK-3).</description></item>
/// <item><term><c>rehydrate</c></term><description><c>DocketRehydration.PageAsync</c> (DK-5).</description></item>
/// <item><term><c>markExecuted</c></term><description><c>ReviewGate.MarkExecutedAsync</c> (AZ-5, AZ-7).</description></item>
/// </list>
/// </remarks>
internal sealed class StepExecutor(GateHarness harness, GivenSpec given)
{
    private readonly Dictionary<string, Guid> _labels = new(StringComparer.Ordinal);
    private Guid? _lastFiled;

    /// <summary>What one step did.</summary>
    internal sealed record StepResult(Refusal? Refusal)
    {
        public Guid? EntryId { get; init; }

        public EvidenceCardRequest? Card { get; init; }

        public JsonObject? Expired { get; init; }

        public JsonObject? Page { get; init; }

        public bool? Found { get; init; }

        /// <summary>
        /// Whether this step was a <b>filing</b> — a <c>file</c>, a <c>wrap-execute</c> or a
        /// <c>resubmit</c>. The card invariants of <c>RUNNER.md</c> §4.2 are checked on every one,
        /// whether or not the fixture mentions them, and "no card was broadcast" is one of the
        /// answers they can give.
        /// </summary>
        public bool IsFiling { get; init; }

        /// <summary>Set when the step kind has no counterpart in this release: the fixture fails, and this says why.</summary>
        public string? NotImplemented { get; init; }

        /// <summary>
        /// The row as it stood when this step filed it. The card invariants compare a card against
        /// the row it was broadcast for, so they need the row at that moment: a later step that
        /// decides the row, folds an amendment into it or expires it does not make the card the gate
        /// broadcast earlier wrong.
        /// </summary>
        public DocketEntry? Filed { get; init; }
    }

    /// <summary>The row a clause is about: the step's own, or the last one filed.</summary>
    public Guid? Current => _lastFiled;

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
                "expireDue" => await ExpireDueAsync(step, conversationId, ct),
                "rehydrate" => await RehydrateAsync(step, conversationId, ct),
                "markExecuted" => await MarkExecutedAsync(step, conversationId, ct),
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

        // CV-4/CV-1, at the moment a host wires a tool up: a write-capable tool the gate cannot
        // stand in front of is refused before anything is proposed. The rule is the framework's —
        // ToolCoverage.Audit — and the facts are the tool's, exactly as an adapter's own audit
        // hands them over.
        if (isWrap)
        {
            Affiant.Core.Services.ToolCoverage.Audit(
                toolName, step.Tool!.WriteCapable, CoverageOf(step.Tool!));
        }

        var strategy = isWrap ? FixtureStrategy.ForTool(step.Tool!) : FixtureStrategy.ForFile(step);
        var operationType = (isWrap ? step.Tool!.EntityId : step.Operation!.EntityId) is null ? "WriteCreate" : "WriteUpdate";

        // The proposed values, as the entity the projection reads. ContextFabric keys entities by
        // EntityId and SchemaDrivenAffidavitProjection looks one up by the strategy's EntityName, so
        // that is the key a host has to use.
        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        if (isWrap)
        {
            // DRIVER.md §3 binds `wrap-execute` to the wrapped-tool surface. What turns a model's
            // tool arguments into sworn provenance is the framework's own ToolArgumentCaptureFilter,
            // so the driver RUNS it — over a ToolInvocationContext shaped the way an adapter's
            // pipeline hands one over — rather than restating what it does. A driver that restated
            // it would supply the answer the fixture is asking for: the shipped filter could grade
            // every argument Inferred at 0.05 and every Sequence A fixture would stay green.
            var registry = services.GetRequiredService<IAffiantToolRegistry>();
            if (registry.Find(toolName) is null)
            {
                registry.Register(new AffiantToolDescriptor(
                    toolName,
                    PluginName: null,
                    Operation: operationType == "WriteUpdate" ? Operation.WriteUpdate : Operation.WriteCreate,
                    EntityType: strategy.EntityName,
                    InferenceStrategy: null));
            }

            var invocation = new ToolInvocationContext
            {
                FunctionName = toolName,
                PluginName = string.Empty,
                Arguments = step.Args!.ToDictionary(
                    kv => kv.Key, kv => Values.ToClr(kv.Value), StringComparer.Ordinal),
                Services = services,
            };

            await services.GetRequiredService<Affiant.Core.Filters.ToolArgumentCaptureFilter>()
                .OnToolInvocationAsync(invocation, _ => Task.CompletedTask, ct);

            foreach (var (name, value) in invocation.Arguments)
            {
                if (value is { } clr) values[name] = clr;
            }
        }
        else
        {
            foreach (var field in step.PreparedFields ?? [])
            {
                // A prepared tag carries the instant it was minted at — the step's own clock. A
                // fixture states a source, a confidence and sometimes a binding; when the tag was
                // minted is not a fixture's to state, it is the moment the host prepared it, and the
                // v0.1 tag requires one.
                var tag = field.Provenance is { } p
                    ? new ProvenanceTag(
                        Enum.Parse<ProvenanceSource>(p.Source),
                        (float)p.Confidence,
                        p.Note,
                        ConversationTurn: null,
                        Binding: Bindings.ToFramework(p.Binding),
                        At: harness.Clock)
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
            services.GetRequiredService<IObservabilityEventStream<AffidavitEmittedEvent>>(),
            services.GetServices<IPreviousValueSource>());

        var entityId = isWrap ? step.Tool!.EntityId : step.Operation!.EntityId;
        var affidavit = projection.Project(fabric, operationType, [], entityId);
        var actor = principal?.Id ?? "(unresolved)";

        // The arguments the model passed, as a host would hand them over: they are part of the
        // material an entry id derives from (GT-4), and the gate is what derives it. A driver that
        // supplied an id of its own would be answering the question the fixture asks.
        var proposal = new WriteProposal(
            toolName,
            harness.Clock,
            affidavit,
            step.Args is null
                ? null
                : step.Args.ToDictionary(kv => kv.Key, kv => Values.ToClr(kv.Value), StringComparer.Ordinal));
        var context = new ReviewContext(
            SessionId: conversationId,
            TenantId: tenantId,
            UserId: actor,
            ReviewerUserId: actor,
            Affidavit: affidavit,
            Channel: given.Ctx.Channel);

        var mark = harness.Transport.Mark();
        var filing = await harness.GateFor(conversationId).FileForReviewAsync(proposal, context, ct);

        var entryId = filing switch
        {
            ReviewFilingResult.RequiresReview r => r.EntryId,
            ReviewFilingResult.Decided d => d.Outcome.DocketId,
            _ => Guid.Empty,
        };

        _lastFiled = entryId;
        Remember(step, entryId);

        var card = harness.Transport.Since(mark)
            .Where(b => b.Event == TransportEvent.EvidenceCardRequest)
            .Select(b => (EvidenceCardRequest)b.Payload)
            .LastOrDefault();

        return new StepResult(null)
        {
            EntryId = entryId,
            Card = card,
            IsFiling = true,
            Filed = await harness.Store.GetDocketEntryAsync(entryId, ct),
        };
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
            Context(step, conversationId, decision.Reason),
            amendments is { Count: > 0 } ? amendments! : null,
            ct);

        var refusal = outcome switch
        {
            null => new Refusal(RefusalCodes.EntryNotFound, $"DocketEntry {entryId} was not found."),
            ReviewOutcome.Refused refused => new Refusal(refused.Code, refused.Detail ?? refused.Code),

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

        var filing = await harness.GateFor(conversationId).ResubmitAsync(entryId.Value, Context(step, conversationId, null), ct);
        var newId = filing switch
        {
            ReviewFilingResult.RequiresReview r => r.EntryId,
            ReviewFilingResult.Decided d => d.Outcome.DocketId,
            _ => entryId.Value,
        };

        _lastFiled = newId;
        Remember(step, newId);
        return new StepResult(null)
        {
            EntryId = newId,
            Card = harness.Transport.CardFor(newId),
            IsFiling = true,
            Filed = await harness.Store.GetDocketEntryAsync(newId, ct),
        };
    }

    private async Task<StepResult> GetAsync(StepSpec step, CancellationToken ct)
    {
        var entryId = Target(step);
        var entry = entryId is null ? null : await harness.Store.GetDocketEntryAsync(entryId.Value, ct);
        return new StepResult(null) { EntryId = entryId, Found = entry is not null };
    }

    private async Task<StepResult> ExpireDueAsync(StepSpec step, string conversationId, CancellationToken ct)
    {
        var scope = new DocketScope(
            step.Scope?.TenantId ?? step.TenantId ?? given.Ctx.TenantId,
            step.Scope?.ConversationId);

        var swept = await harness.Store.ExpireDueAsync(harness.Clock, scope, step.Limit ?? int.MaxValue, ct);
        return new StepResult(null)
        {
            Expired = new JsonObject { ["count"] = swept.Expired.Count, ["more"] = swept.More },
        };
    }

    private async Task<StepResult> MarkExecutedAsync(StepSpec step, string conversationId, CancellationToken ct)
    {
        var entryId = Target(step);
        if (entryId is null || entryId == Guid.Empty)
        {
            return new StepResult(new Refusal(RefusalCodes.EntryNotFound, "No entry has been filed for this step to act on."));
        }

        var outcome = await harness.GateFor(conversationId).MarkExecutedAsync(
            entryId.Value,
            step.Outcome == "failed" ? ExecutionOutcome.Failed : ExecutionOutcome.Executed,
            step.Detail,
            Context(step, conversationId, null),
            ct);

        _lastFiled = entryId;
        Remember(step, entryId.Value);
        return new StepResult(outcome is ReviewOutcome.Refused refused
            ? new Refusal(refused.Code, refused.Detail ?? refused.Code)
            : null)
        {
            EntryId = entryId,
        };
    }

    /// <summary>
    /// The act's own context: who is acting, from which tenant, in which conversation and on which
    /// channel — passed at the call site, never resolved from ambient state (AZ-2). When the act
    /// happened is the gate's own reading of the fixture's clock, which the harness drives.
    /// </summary>
    private DecisionContext Context(StepSpec step, string conversationId, string? reason)
    {
        var spec = step.PrincipalStated ? step.Principal : given.Ctx.Principal;
        Principal? principal = spec switch
        {
            null => null,
            { Kind: "member" } => new Principal.Member(spec.Id),
            _ => new Principal.Service(
                spec.Id,
                spec.Relay is { } relay ? new RelayAssertion(relay.ChannelIdentity, relay.MessageId) : null,
                spec.AssertedMember),
        };

        return new DecisionContext(
            principal,
            step.TenantId ?? given.Ctx.TenantId,
            conversationId,
            given.Ctx.Channel,
            reason);
    }

    private async Task<StepResult> RehydrateAsync(StepSpec step, string conversationId, CancellationToken ct)
    {
        var scope = new DocketScope(
            step.Scope?.TenantId ?? step.TenantId ?? given.Ctx.TenantId,
            step.Scope?.ConversationId ?? conversationId);

        var page = await DocketRehydration.PageAsync(
            harness.Store,
            scope,
            new DocketPage(step.Page!.Limit, step.Page.Cursor),
            ct);

        var statuses = new JsonArray();
        foreach (var entry in page.Items)
        {
            statuses.Add(Observation.Status(entry.Status));
        }

        return new StepResult(null)
        {
            Page = new JsonObject
            {
                ["count"] = page.Items.Count,
                ["more"] = page.More,
                ["statuses"] = statuses,
            },
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
    /// Why the gate cannot cover <paramref name="tool"/>, or <see langword="null"/> when it can
    /// (CV-4). A tool the provider runs, a hosted MCP write, and a write-capable tool with no
    /// execute step for the gate to replace are the three the rule names.
    /// </summary>
    private static Affiant.Abstractions.Models.CoverageCategory? CoverageOf(ToolSpec tool) =>
        string.Equals(tool.ExecutedBy, "provider", StringComparison.Ordinal)
            ? Affiant.Abstractions.Models.CoverageCategory.ProviderExecuted
            : tool.HostedMcp
                ? Affiant.Abstractions.Models.CoverageCategory.HostedMcp
                : tool.OmitExecute
                    ? Affiant.Abstractions.Models.CoverageCategory.NoExecute
                    : null;
}
