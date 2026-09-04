namespace Affiant.Extensions.AI.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Extensions;
using Affiant.Extensions.AI.Tests.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Acceptance criterion 4 of the Microsoft.Extensions.AI adapter design brief
/// (<c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>): "review-gate
/// semantics proven at the new seam: block, replace-result, queue (docket round-trip), deterministic
/// short-circuit, hosted-tool audit throw, double-wrap throw — each with a test." This file is that
/// list, one test per power, in that order, so a reader can check the criterion off against it
/// without reconstructing the mapping from test names.
///
/// <para>
/// <b>Why these are not the same tests as the ones next door.</b> Every power here is exercised
/// through the real <see cref="FunctionInvokingChatClient"/> loop over a scripted
/// <see cref="IChatClient"/>, driven by the framework's own <c>ReviewGate</c> and approval policy —
/// not by a hand-written filter standing in for one. <see cref="AffiantDelegatingAIFunctionTests"/>
/// proves the seam mechanically <em>can</em> replace a result and end a turn (a test filter does the
/// asking); this file proves the <em>review gate itself</em> is what asks, over an in-memory docket,
/// and that the docket entry and the Evidence Card that the reviewer will act on genuinely exist
/// afterwards. <c>Extensions/HostedToolAuditTests</c> and <c>Extensions/WithAffiantWiringTests</c>
/// own the exhaustive wire-up-refusal matrices; the last two tests here pin only the one fact
/// criterion 4 asks for — that each refusal happens at wire-up, before any turn can run.
/// </para>
///
/// <para>
/// The Microsoft Agent Framework counterpart is
/// <c>tests/Affiant.AgentFramework.Tests/Filters/ReviewGateFilterMafBoundaryTests.cs</c>, which makes
/// the same point one layer lower (its middleware invoked directly, with a hand-built
/// <c>WriteProposal</c> as the tool's return value). The M.E.AI seam is wrapped rather than
/// middleware-shaped, so the equivalent here runs the whole client, which is strictly the stronger
/// statement: the proposal JSON is produced by a real reflected tool and the turn-ending verdict has
/// to survive all the way back to <see cref="FunctionInvokingChatClient"/>'s own loop check.
/// </para>
/// </summary>
public class ReviewGateSemanticsAtTheSeamTests
{
    /// <summary>
    /// The single model-facing message <c>Affiant.Core.Filters.ReviewGateFilter</c> substitutes on
    /// <c>ReviewFilingResult.RequiresReview</c>. Asserted by content rather than referenced from the
    /// filter, which keeps it <c>private const</c> — a change to the wording is a change to what
    /// every host's model sees, and should break a test.
    /// </summary>
    private const string TurnEndingMessage =
        "This action has been filed for review — check the Evidence Card to approve, reject, or amend it.";

    // ── 1. Block ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Block.</b> A write proposal whose approval policy demands a human reviewer ends the model's
    /// turn at the seam: the tool ran, the proposal was filed, and the loop never goes back to the
    /// model. The scripted client's call count is the witness — 1 means the turn ended here, 2 means
    /// the model was asked for a follow-up while a decision is pending, which is precisely the
    /// "kept reasoning past an unapproved write" failure the gate exists to prevent.
    /// </summary>
    [Fact]
    public async Task Block_ReviewerConfirmationRequired_EndsTheTurnAfterTheWriteToolRuns()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var transport = new RecordingStreamingTransport();
        var inference = new StubInferenceChatClient();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools,
            approvalPolicy: new ReviewerConfirmationPolicy(),
            transport: transport,
            inferenceChatClient: inference);

        await RunOneTurnAsync(sp, client);

        Assert.Equal(["gizmo"], tools.CreateCalls);
        Assert.Equal(1, client.CallCount);
        // Task inference for the write tool ran on its own client — counted here so the witness above
        // is unambiguous rather than accidentally quiet.
        Assert.Equal(1, inference.CallCount);
    }

    /// <summary>
    /// The paired control for the test above: with the fixture's default standing-order policy the
    /// filing resolves without a reviewer, so the gate deliberately does NOT terminate and the loop
    /// continues normally. Without this pairing, the block test would also pass against an
    /// implementation that ended every turn containing a write tool.
    /// </summary>
    [Fact]
    public async Task Block_StandingOrderAutoApproval_DoesNotEndTheTurn()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools, inferenceChatClient: new StubInferenceChatClient());

        await RunOneTurnAsync(sp, client);

        Assert.Equal(2, client.CallCount);
        Assert.Equal(ReviewStatus.Approved, Assert.Single(docket.Filed).Status);
    }

    // ── 2. Replace-result ────────────────────────────────────────────────────

    /// <summary>
    /// <b>Replace-result.</b> What the model is told is the gate's turn-ending message, not the tool's
    /// own <c>WriteProposal</c> JSON. The negative half matters as much as the positive one: a
    /// proposal that reaches the model verbatim invites it to narrate the write as already done.
    /// </summary>
    [Fact]
    public async Task ReplaceResult_TheModelSeesTheGatesMessage_NotTheWriteProposalItFiled()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var transport = new RecordingStreamingTransport();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools, approvalPolicy: new ReviewerConfirmationPolicy(), transport: transport);

        var response = await RunOneTurnAsync(sp, client);

        var modelVisible = SingleFunctionResult(response);
        Assert.Equal(TurnEndingMessage, modelVisible);
        Assert.DoesNotContain("$type", modelVisible, StringComparison.Ordinal);
        Assert.DoesNotContain("affidavit", modelVisible, StringComparison.OrdinalIgnoreCase);
    }

    // ── 3. Queue — the docket round trip ─────────────────────────────────────

    /// <summary>
    /// <b>Queue.</b> The full round trip a queued write makes: the proposal is filed as a Pending
    /// docket entry, an Evidence Card for that same entry is broadcast to the session's group, and a
    /// reviewer's decision arriving afterwards is applied to that entry.
    ///
    /// <para>
    /// The decision is routed through <c>ReviewGate.HandleDecisionAsync</c>'s no-live-waiter path,
    /// which is the only path that exists under the framework's non-blocking filing default: nothing
    /// is awaiting the reviewer, so the decision has to be replayed through the docket store. That is
    /// what makes this a round trip rather than three unrelated assertions — the entry id the card
    /// carried is the id the decision is applied to.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Queue_ProposalFiled_EvidenceCardEmitted_AndALaterDecisionIsApplied()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var transport = new RecordingStreamingTransport();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools, approvalPolicy: new ReviewerConfirmationPolicy(), transport: transport);

        await RunOneTurnAsync(sp, client);

        // (a) proposal filed — Pending, awaiting a human, under the tool's own name.
        var filed = Assert.Single(docket.Filed);
        Assert.Equal("CreateWidget", filed.OperationType);
        Assert.Equal(ReviewStatus.Pending, filed.Status);
        Assert.Equal("session-test", filed.SessionId);

        // (b) Evidence Card emitted — on the session group, for that same entry.
        var broadcast = Assert.Single(
            transport.Broadcasts, b => b.Event == TransportEvent.EvidenceCardRequest);
        Assert.Equal("session-test", broadcast.GroupId);
        var card = Assert.IsType<EvidenceCardRequest>(broadcast.Payload);
        Assert.Equal(filed.EntryId, card.DocketId);

        // (c) decision applied — the reviewer approves the entry the card named.
        using var scope = sp.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ReviewGate>();
        var (outcome, _) = await gate.HandleDecisionAsync(
            card.DocketId,
            ApprovalDecision.Approved,
            new DecisionContext(new Principal.Member("reviewer-1"), filed.TenantId));

        Assert.IsType<ReviewOutcome.Approved>(outcome);
        var resolved = Assert.Single(docket.Filed);
        Assert.Equal(ReviewStatus.Approved, resolved.Status);
    }

    // ── 4. Deterministic short-circuit ───────────────────────────────────────

    /// <summary>
    /// <b>Deterministic short-circuit.</b> A matching <see cref="IIntentInterceptor"/> answers before
    /// the tool body runs, so a write tool never executes and nothing reaches the docket. This is the
    /// pre-tool half of the gate's power: not "the write was reviewed" but "the write never happened",
    /// decided without an LLM round trip.
    /// </summary>
    [Fact]
    public async Task DeterministicShortCircuit_AnswersBeforeTheWriteToolRuns_AndNothingIsFiled()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools, services =>
            services.AddSingleton<IIntentInterceptor>(new AlwaysMatchingInterceptor("handled deterministically")));

        var response = await RunOneTurnAsync(sp, client);

        Assert.Empty(tools.CreateCalls);
        Assert.Empty(docket.Filed);
        Assert.Equal("handled deterministically", SingleFunctionResult(response));
    }

    // ── 5. Hosted-tool audit ─────────────────────────────────────────────────

    /// <summary>
    /// <b>Hosted-tool audit throws at wire-up.</b> A provider-executed tool cannot be wrapped, so it
    /// cannot be gated — the host is told while it is still wiring, not after the ungoverned write.
    /// The assertion that matters beyond the throw is the second one: the refusal happens before a
    /// governed <see cref="ChatOptions"/> ever exists, so there is no half-wired object a caller
    /// could carry into a turn.
    ///
    /// <para>The refusal matrix (acknowledgement, registry cleanliness, corrected retry) is
    /// <c>Extensions/HostedToolAuditTests</c>'s; this pins only criterion 4's claim.</para>
    /// </summary>
    [Fact]
    public void HostedToolAudit_ThrowsAtWireUp_BeforeAnyGovernedOptionsExist()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?>());
        using var sp = AffiantTestHost.Build(client, docket, tools);

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var options = new ChatOptions { Tools = [new HostedCodeInterpreterTool(), .. catalog.Functions] };

        var ex = Assert.Throws<InvalidOperationException>(() => options.WithAffiant(sp, catalog));

        Assert.Contains("code_interpreter", ex.Message, StringComparison.Ordinal);
        // The caller's own options are untouched: nothing was wrapped, so nothing about this object
        // can be mistaken for governed.
        Assert.DoesNotContain(options.Tools!, t => t is Affiant.Extensions.AI.Filters.IAffiantWrappedFunction);
    }

    // ── 6. Double-wrap guard ─────────────────────────────────────────────────

    /// <summary>
    /// <b>Double-wrap throws with an actionable message.</b> Wrapping one tool twice runs the neutral
    /// onion twice for one logical call — double-tagged provenance, inference fired twice, the same
    /// proposal filed onto the docket twice. Nothing downstream reports that as an error, so the
    /// message has to carry the fix, including the one direction the guard cannot detect: the same
    /// catalog wired by both this adapter and <c>Affiant.AgentFramework</c>.
    ///
    /// <para>The full guard matrix is <c>Extensions/WithAffiantWiringTests</c>'s; this pins the throw
    /// and the actionability criterion 4 asks for.</para>
    /// </summary>
    [Fact]
    public void DoubleWrap_ThrowsAtWireUp_WithAMessageThatNamesTheFixAndTheUndetectableCase()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?>());
        using var sp = AffiantTestHost.Build(client, docket, tools);

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        var ex = Assert.Throws<InvalidOperationException>(
            () => wired.WithAffiant(sp, new AffiantToolCatalog([], [])));

        // Names the offending tool, the fix, and the cross-adapter case no guard can see.
        Assert.Contains("CreateWidget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exactly once", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Affiant.AgentFramework", ex.Message, StringComparison.Ordinal);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static async Task<ChatResponse> RunOneTurnAsync(IServiceProvider sp, IChatClient inner)
    {
        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        using var pipeline = new ChatClientBuilder(inner).UseFunctionInvocation().Build(sp);

        return await pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "please create the widget")], wired);
    }

    private static string SingleFunctionResult(ChatResponse response) =>
        response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Single()
            .Result?.ToString() ?? string.Empty;

    private sealed class AlwaysMatchingInterceptor(string answer) : IIntentInterceptor
    {
        public Task<bool> MatchesAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<object?> HandleAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(answer);
    }
}
