namespace Affiant.Extensions.AI.Tests.Filters;

using Affiant.Abstractions.Models;
using Affiant.Extensions.AI.Attributes;
using Affiant.Extensions.AI.Extensions;
using Affiant.Extensions.AI.Filters;
using Affiant.Extensions.AI.Tests.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The <em>invoke-time</em> half of the double-wrap guard (design decision 6).
///
/// <para>
/// <b>Why a second half is needed.</b> <c>WithAffiant</c>'s wire-up check is a top-level type test:
/// <c>tools.OfType&lt;IAffiantWrappedFunction&gt;()</c>. It sees an Affiant wrapper sitting directly on
/// <see cref="ChatOptions.Tools"/> and nothing else. Put one ordinary
/// <see cref="DelegatingAIFunction"/> in between — a host's telemetry, retry, redaction or
/// argument-coercion layer, which is exactly the shape the Microsoft Agent Framework itself uses —
/// and the marker is hidden. A second <c>WithAffiant</c> over those tools then succeeds, and one
/// logical tool call runs the neutral onion twice: provenance double-tagged, inference fired twice,
/// and the same write proposal filed onto the docket twice. Nothing downstream reports it, because
/// nothing downstream can tell.
/// </para>
///
/// <para>
/// <b>The guard.</b> <c>AffiantDelegatingAIFunction</c> publishes an <c>AsyncLocal</c> record of the
/// onion in flight, tagged with the <see cref="FunctionInvocationContext"/> it is running for. A
/// wrapper entered while a record for the <em>same</em> context is live refuses. Reference equality on
/// the context is what separates the two shapes that look identical from inside a wrapper: nested
/// wrappers around one logical call share the context instance, whereas a tool body that legitimately
/// runs its own governed sub-agent gets a fresh instance from that sub-agent's own
/// <see cref="FunctionInvokingChatClient"/>. Both are pinned below.
/// </para>
///
/// <para>
/// The wire-up marker is kept as the earlier, friendlier check — see
/// <c>Extensions/WithAffiantWiringTests</c> — because it fails before a turn ever runs. This file
/// covers what it cannot see.
/// </para>
/// </summary>
public class NestedWrapperReentrancyTests
{
    /// <summary>
    /// <b>The hole, closed.</b> Wire Affiant, hide the wrappers behind one layer of host middleware,
    /// wire Affiant again over a second catalog on the same options. The wire-up guard cannot see the
    /// first wrapper, so wiring succeeds — and the resulting tool is Affiant-wrapped twice.
    ///
    /// <para>
    /// Fail-first shape: before the invoke-time guard existed this test observed one tool-body run and
    /// <b>two</b> docket entries for it. With the guard, the nested wrapper refuses before the tool
    /// body runs, the refusal travels back as <c>ToolErrorFilter</c>'s error envelope carrying the
    /// actionable message, and nothing is filed. Refusing costs the host the call; the alternative
    /// costs it a duplicate write proposal that a reviewer has to notice by hand.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HostMiddlewareHidingAnAffiantWrapper_DoesNotLetASecondOnionRun()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools,
            configure: services => services.AddSingleton(new GadgetTools()),
            inferenceChatClient: new StubInferenceChatClient());

        var doubleWrapped = WireTwiceThroughHostMiddleware(sp);

        using var pipeline = new ChatClientBuilder(client).UseFunctionInvocation().Build(sp);
        var response = await pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "please create the widget")], doubleWrapped);

        // The tool body never ran and nothing reached the docket: the refusal is the whole outcome.
        Assert.Empty(tools.CreateCalls);
        Assert.Empty(docket.Filed);

        // The model is told why, in the guard's own words, through the framework's one-failure
        // envelope — not with a bare "something went wrong".
        var toolResult = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Single()
            .Result?.ToString() ?? string.Empty;
        Assert.Contains("wrapped by Affiant twice", toolResult, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first control: one Affiant wrapper with host middleware <em>outside</em> it runs completely
    /// normally. Without this, the test above would also pass against a guard that refused any tool
    /// reached through a <see cref="DelegatingAIFunction"/> at all — which would break every host that
    /// composes its own function middleware, i.e. the mainstream case.
    /// </summary>
    [Fact]
    public async Task OneAffiantWrapperBehindHostMiddleware_RunsTheOnionExactlyOnce()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools, inferenceChatClient: new StubInferenceChatClient());

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        // Host middleware wrapped around Affiant's wrapper, not between two of them.
        var hosted = wired.Clone();
        hosted.Tools = [.. wired.Tools!.Select(t => t is AIFunction f ? new HostMiddlewareFunction(f) : t)];

        using var pipeline = new ChatClientBuilder(client).UseFunctionInvocation().Build(sp);
        await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "please create the widget")], hosted);

        Assert.Equal(["gizmo"], tools.CreateCalls);
        Assert.Equal(ReviewStatus.Approved, Assert.Single(docket.Filed).Status);
    }

    /// <summary>
    /// The second control, and the one the reference-equality design exists for: a tool body that runs
    /// its own governed sub-agent. The inner <see cref="FunctionInvokingChatClient"/> publishes a fresh
    /// <see cref="FunctionInvocationContext"/>, so the inner wrapper is running a genuinely different
    /// logical tool call, not this one a second time — and its onion must run, filing the sub-agent's
    /// write for review exactly as it would at the top level.
    ///
    /// <para>
    /// A guard keyed on "is any onion running?" rather than "is <em>this call's</em> onion running?"
    /// would refuse here and quietly make nested agents ungovernable. That is the failure this test
    /// stands in front of.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AGovernedSubAgentStartedInsideAToolBody_StillRunsItsOwnOnion()
    {
        var tools = new WidgetTools();
        var delegating = new SubAgentTools();
        var docket = new FakeDocketStore();
        var outerClient = new ScriptedChatClient(
            "DelegateToSubAgent", new Dictionary<string, object?> { ["task"] = "make a widget" });
        using var sp = AffiantTestHost.Build(
            outerClient, docket, tools,
            configure: services => services.AddSingleton(delegating),
            inferenceChatClient: new StubInferenceChatClient());

        var outerCatalog = AffiantToolCatalog.FromType<SubAgentTools>();
        var innerCatalog = AffiantToolCatalog.FromType<WidgetTools>();
        var outerOptions = new ChatOptions { Tools = [.. outerCatalog.Functions] }.WithAffiant(sp, outerCatalog);
        var innerOptions = new ChatOptions { Tools = [.. innerCatalog.Functions] }.WithAffiant(sp, innerCatalog);

        delegating.Services = sp;
        delegating.InnerOptions = innerOptions;
        delegating.InnerClient = new ScriptedChatClient(
            "CreateWidget", new Dictionary<string, object?> { ["name"] = "nested-gizmo" });

        using var pipeline = new ChatClientBuilder(outerClient).UseFunctionInvocation().Build(sp);
        await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "delegate it")], outerOptions);

        Assert.Equal(1, delegating.Runs);
        Assert.Equal(["nested-gizmo"], tools.CreateCalls);
        Assert.Equal(ReviewStatus.Approved, Assert.Single(docket.Filed).Status);
    }

    /// <summary>
    /// The third control: the guard's record must be torn down when an onion finishes, not left
    /// standing for the rest of the turn. Two sequential tool calls in one conversation both have to
    /// run — an <c>AsyncLocal</c> that leaked forward would refuse the second one and turn a working
    /// multi-tool agent into a one-tool agent.
    /// </summary>
    [Fact]
    public async Task ASecondToolCallInTheSameConversation_IsNotMistakenForARepeat()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "first" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools, inferenceChatClient: new StubInferenceChatClient());

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);
        wired.ConversationId = "conversation-A";

        using (var pipeline = new ChatClientBuilder(client).UseFunctionInvocation().Build(sp))
            await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "one")], wired);

        var again = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "second" });
        using (var pipeline = new ChatClientBuilder(again).UseFunctionInvocation().Build(sp))
            await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "two")], wired);

        Assert.Equal(["first", "second"], tools.CreateCalls);
        Assert.Equal(2, docket.Filed.Count);
    }

    /// <summary>
    /// Wires Affiant, hides the resulting wrappers behind one layer of host middleware, and wires
    /// Affiant again — over a <em>second</em> tool type, so the repro isolates the marker hole rather
    /// than tripping <c>AffiantToolRegistry</c>'s unrelated "descriptor already registered" refusal.
    /// This is the realistic shape of the mistake: a host that governs two tool catalogs and composes
    /// its own middleware between the two wire-ups.
    /// </summary>
    private static ChatOptions WireTwiceThroughHostMiddleware(IServiceProvider sp)
    {
        var widgets = AffiantToolCatalog.FromType<WidgetTools>();
        var gadgets = AffiantToolCatalog.FromType<GadgetTools>();

        var once = new ChatOptions { Tools = [.. widgets.Functions] }.WithAffiant(sp, widgets);

        var hidden = once.Clone();
        hidden.Tools = [.. once.Tools!.Select(t => t is AIFunction f ? new HostMiddlewareFunction(f) : t)];

        return hidden.WithAffiant(sp, gadgets);
    }

    /// <summary>
    /// The stand-in for any host <see cref="DelegatingAIFunction"/> — telemetry, retry, redaction,
    /// argument coercion. Deliberately behaviour-free: what matters is only that it is a type Affiant
    /// does not recognise, sitting where the marker check looks.
    /// </summary>
    private sealed class HostMiddlewareFunction(AIFunction inner) : DelegatingAIFunction(inner);

    /// <summary>
    /// A second tool type, so a second <c>WithAffiant</c> has descriptors of its own to register.
    /// </summary>
    internal sealed class GadgetTools
    {
        public string LookUpGadget(string name) => $"gadget:{name}";
    }

    /// <summary>
    /// A tool whose body runs a whole nested, Affiant-governed chat loop — the "agent calls an agent"
    /// shape. The host supplies the inner client and options after wire-up, because both are built
    /// from the same container this tool is resolved out of.
    /// </summary>
    internal sealed class SubAgentTools
    {
        public IServiceProvider? Services { get; set; }

        public IChatClient? InnerClient { get; set; }

        public ChatOptions? InnerOptions { get; set; }

        public int Runs { get; private set; }

        [AffiantToolName("DelegateToSubAgent")]
        public async Task<string> DelegateToSubAgent(string task)
        {
            Runs++;
            using var loop = new ChatClientBuilder(InnerClient!).UseFunctionInvocation().Build(Services!);
            await loop.GetResponseAsync([new ChatMessage(ChatRole.User, task)], InnerOptions!);
            return "delegated";
        }
    }
}
