namespace Affiant.Extensions.AI.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Extensions;
using Affiant.Extensions.AI.Tests.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Pins a <b>known limitation</b> of this seam — deliberately, as current behaviour, so that the
/// framework-wide fix it is waiting on has something to flip.
///
/// <para>
/// <b>The limitation.</b> Affiant's conversation state (<c>IContextFabric</c>) is registered
/// <em>scoped</em>, on the assumption that whoever runs a tool call owns a per-conversation (or at
/// least per-turn) DI scope. At the Microsoft.Extensions.AI function-calling seam that assumption
/// does not hold. <see cref="FunctionInvokingChatClient"/> sets
/// <see cref="AIFunctionArguments.Services"/> to the provider the <see cref="ChatClientBuilder"/> was
/// built from — in the documented wiring, the application <em>root</em> provider — and
/// <c>AffiantDelegatingAIFunction</c> passes that straight to
/// <see cref="ToolInvocationPipeline.RunAsync"/> as the ambient provider. The pipeline therefore
/// never takes its own <c>CreateScope()</c> branch here, and the scoped fabric resolves to one
/// process-global instance shared by every conversation in the process.
/// </para>
///
/// <para>
/// <b>What that costs.</b> <c>Affiant.Core.Filters.InferenceTriggerFilter</c> dedups inference per
/// <c>(ConversationId, FunctionName, TurnNumber)</c> and, when <c>ConversationId</c> is null, falls
/// back to the fabric instance's identity hash. One global fabric plus a null conversation id
/// collapses every conversation onto one key, so the second and every later conversation's write-tool
/// inference is <em>silently</em> skipped — no exception, no warning, just a thinner affidavit.
/// <c>ToolArgumentCaptureFilter</c>'s provenance chains, keyed on the bare argument name, are
/// overwritten across conversations for the same reason.
/// </para>
///
/// <para>
/// <b>Why this is not fixed in this package.</b> The defect is framework-wide, not adapter-local:
/// <c>Affiant.AgentFramework</c>'s <c>AffiantFunctionInvocationMiddleware</c> and
/// <c>Affiant.SemanticKernel</c>'s <c>AffiantFunctionInvocationBridge</c> source their ambient
/// provider identically and share it. A real fix — a per-turn scope owned by the adapter, or
/// namespacing the idempotency key and the provenance chains by conversation — re-shapes all three
/// adapters at once and belongs to its own wave. The <em>host-side</em> mitigation is complete and
/// costs nothing: set <see cref="ChatOptions.ConversationId"/> per conversation, which the package
/// README, <c>WithAffiant</c>'s XML docs and <c>AffiantDelegatingAIFunction</c>'s all now say
/// explicitly. The control test below is that mitigation, proven.
/// </para>
/// </summary>
public class ConversationScopeBleedAtTheSeamTests
{
    /// <summary>
    /// <b>The bleed, pinned.</b> Two separate conversations, each its own chat client and its own
    /// turn, wired exactly as the package README's quickstart shows and with no
    /// <see cref="ChatOptions.ConversationId"/> set. Both tool bodies run and both writes reach the
    /// docket — so nothing looks wrong — but task inference runs only for the first conversation.
    ///
    /// <para>
    /// If this test ever fails with <c>2</c> observed, the framework-wide scope fix has landed and
    /// this file should be deleted along with the KNOWN LIMITATION notes it is cited from.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WithoutAConversationId_EveryConversationAfterTheFirstSilentlySkipsWriteToolInference()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var inference = new StubInferenceChatClient();
        var first = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "alice" });
        using var sp = AffiantTestHost.Build(first, docket, tools, inferenceChatClient: inference);

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        await RunTurnAsync(sp, first, wired);

        var second = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "bob" });
        await RunTurnAsync(sp, second, wired);

        // Both conversations really did run the tool and file a write — the skip is invisible from
        // everywhere except the inference count.
        Assert.Equal(["alice", "bob"], tools.CreateCalls);
        Assert.Equal(2, docket.Filed.Count);

        // ...and here it is: one inference call for two write-tool invocations in two conversations.
        Assert.Equal(1, inference.CallCount);
    }

    /// <summary>
    /// <b>The mitigation, proven.</b> Identical to the test above except each conversation carries its
    /// own <see cref="ChatOptions.ConversationId"/>. That alone gives
    /// <c>InferenceTriggerFilter</c> a real per-conversation idempotency namespace, so both
    /// conversations infer. This is the pairing that makes the test above a statement about the
    /// missing conversation id rather than about inference being broken outright.
    /// </summary>
    [Fact]
    public async Task WithAConversationId_EachConversationRunsItsOwnWriteToolInference()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var inference = new StubInferenceChatClient();
        var first = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "alice" });
        using var sp = AffiantTestHost.Build(first, docket, tools, inferenceChatClient: inference);

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        var conversationA = wired.Clone();
        conversationA.ConversationId = "conversation-A";
        await RunTurnAsync(sp, first, conversationA);

        var second = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "bob" });
        var conversationB = wired.Clone();
        conversationB.ConversationId = "conversation-B";
        await RunTurnAsync(sp, second, conversationB);

        Assert.Equal(["alice", "bob"], tools.CreateCalls);
        Assert.Equal(2, docket.Filed.Count);
        Assert.Equal(2, inference.CallCount);
    }

    /// <summary>
    /// The root cause underneath both tests above, pinned directly rather than inferred from the
    /// inference count: the provider the neutral pipeline runs on at this seam <em>is</em> the host's
    /// root provider, so the pipeline's own per-invocation scope branch is unreachable here and the
    /// scoped <see cref="IContextFabric"/> is one instance for the whole process.
    ///
    /// <para>
    /// Worth pinning separately because the two symptoms differ in visibility — one skipped inference
    /// call is easy to miss, one shared fabric explains it — and because a future fix will change
    /// this assertion first.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ThePipelineRunsOnTheHostsRootProvider_SoTheScopedContextFabricIsProcessGlobal()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var probe = new AmbientProviderProbe();
        var first = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "alice" });
        using var sp = AffiantTestHost.Build(
            first, docket, tools,
            configure: services => services.AddSingleton<IToolInvocationFilter>(probe),
            inferenceChatClient: new StubInferenceChatClient());

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        await RunTurnAsync(sp, first, wired);
        var second = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "bob" });
        await RunTurnAsync(sp, second, wired);

        Assert.Equal(2, probe.Providers.Count);
        Assert.All(probe.Providers, provider => Assert.Same(sp, provider));
        Assert.Same(probe.Fabrics[0], probe.Fabrics[1]);
    }

    private static Task<ChatResponse> RunTurnAsync(IServiceProvider sp, IChatClient inner, ChatOptions options)
    {
        using var pipeline = new ChatClientBuilder(inner).UseFunctionInvocation().Build(sp);
        return pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "please create the widget")], options);
    }

    /// <summary>
    /// Records the provider and conversation fabric each tool invocation actually ran on. Registered
    /// as an extra <see cref="IToolInvocationFilter"/>, so it observes the real onion rather than
    /// re-deriving what the pipeline would have chosen.
    /// </summary>
    private sealed class AmbientProviderProbe : IToolInvocationFilter
    {
        public List<IServiceProvider> Providers { get; } = [];

        public List<IContextFabric> Fabrics { get; } = [];

        public Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            Providers.Add(context.Services!);
            Fabrics.Add(context.Services!.GetRequiredService<IContextFabric>());
            return next(context);
        }
    }
}
