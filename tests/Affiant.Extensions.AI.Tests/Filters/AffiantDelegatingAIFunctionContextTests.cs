namespace Affiant.Extensions.AI.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Extensions;
using Affiant.Extensions.AI.Filters;
using Affiant.Extensions.AI.Tests.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// What <see cref="AffiantDelegatingAIFunction"/> reads from — and writes back to — the ambient
/// <see cref="FunctionInvokingChatClient.CurrentContext"/>, which is Microsoft.Extensions.AI's
/// analog of Semantic Kernel's <c>Kernel.Data</c> and the carrier design decision 1 rests on.
///
/// <para>
/// The seam tests next door prove Affiant's own verdicts reach the loop. These prove the two things
/// that only go wrong when someone <em>else</em> is also using the carrier, or when the loop-scoped
/// inputs are dropped on the way in — both silent failures, neither visible in a passing chat.
/// </para>
/// </summary>
public class AffiantDelegatingAIFunctionContextTests
{
    /// <summary>
    /// affiant#25, the OR-not-overwrite rule, at this seam. Something downstream of Affiant — another
    /// wrapping layer, or the host's own function-invocation configuration — may set
    /// <c>Terminate</c> on the shared context while the tool body runs. Affiant's own verdict is
    /// <c>false</c> here, so an implementation that <em>assigned</em> its verdict rather than OR-ing
    /// it would silently clear the other party's decision and the loop would go back to the model.
    /// The scripted client's call count is the witness: 1 means the turn ended as the downstream
    /// party asked.
    /// </summary>
    [Fact]
    public async Task DownstreamTerminateSetDuringTheToolBody_IsPreserved()
    {
        var docket = new FakeDocketStore();
        var tools = new WidgetTools();
        var client = new ScriptedChatClient("Downstream", new Dictionary<string, object?>());
        using var sp = AffiantTestHost.Build(client, docket, tools);

        var inner = AIFunctionFactory.Create(
            (Func<string>)(() =>
            {
                // Stand-in for a downstream layer: it runs inside the same invocation, on the same
                // ambient context object, after Affiant's pre-tool filters and before its post-tool
                // ones.
                FunctionInvokingChatClient.CurrentContext!.Terminate = true;
                return "downstream ran";
            }),
            name: "Downstream");

        await RunAsync(sp, client, inner);

        Assert.Equal(1, client.CallCount);
    }

    /// <summary>
    /// The mirror image: with nobody downstream terminating and no Affiant filter terminating
    /// either, the flag must stay false. Without this, the test above would also pass against an
    /// implementation that simply hardcoded <c>Terminate = true</c>.
    /// </summary>
    [Fact]
    public async Task NoDownstreamTerminate_LeavesTheLoopRunning()
    {
        var docket = new FakeDocketStore();
        var tools = new WidgetTools();
        var client = new ScriptedChatClient("Downstream", new Dictionary<string, object?>());
        using var sp = AffiantTestHost.Build(client, docket, tools);

        var inner = AIFunctionFactory.Create((Func<string>)(() => "quiet"), name: "Downstream");

        await RunAsync(sp, client, inner);

        Assert.Equal(2, client.CallCount);
    }

    /// <summary>
    /// The loop-scoped inputs the wrapper lifts off the ambient context and onto the neutral request.
    /// <c>ConversationId</c> in particular is load-bearing rather than decorative: it is
    /// <c>InferenceTriggerFilter</c>'s idempotency namespace, and when it is missing the key collapses
    /// to the fabric instance hash and de-duplicates across unrelated conversations.
    /// </summary>
    [Fact]
    public async Task ConversationIdAndTurnNumber_ReachTheNeutralContext()
    {
        var docket = new FakeDocketStore();
        var tools = new WidgetTools();
        var client = new ScriptedChatClient("Downstream", new Dictionary<string, object?>());
        var capture = new CapturingFilter();
        using var sp = AffiantTestHost.Build(client, docket, tools,
            services => services.AddScoped<IToolInvocationFilter>(_ => capture));

        var inner = AIFunctionFactory.Create((Func<string>)(() => "ok"), name: "Downstream");

        await RunAsync(sp, client, inner, conversationId: "conv-42");

        Assert.NotNull(capture.Captured);
        Assert.Equal("conv-42", capture.Captured!.ConversationId);
        Assert.Equal(0, capture.Captured.TurnNumber);
        Assert.NotEmpty(capture.Captured.History);
    }

    /// <summary>
    /// Degraded mode: invoked outside any <see cref="FunctionInvokingChatClient"/>, there is no
    /// ambient context to read. The pipeline must still run in full — that is the bypass resistance
    /// the wrapping design buys — with the loop-scoped inputs left empty rather than faked.
    /// </summary>
    [Fact]
    public async Task InvokedOutsideTheLoop_RunsThePipelineWithEmptyLoopScopedInputs()
    {
        var docket = new FakeDocketStore();
        var tools = new WidgetTools();
        var client = new ScriptedChatClient("Downstream", new Dictionary<string, object?>());
        var capture = new CapturingFilter();
        using var sp = AffiantTestHost.Build(client, docket, tools,
            services => services.AddScoped<IToolInvocationFilter>(_ => capture));

        var inner = AIFunctionFactory.Create((Func<string>)(() => "ok"), name: "Downstream");
        var wrapped = new AffiantDelegatingAIFunction(
            inner,
            sp.GetRequiredService<ToolInvocationPipeline>(),
            sp.GetRequiredService<IAffiantToolRegistry>());

        Assert.Null(FunctionInvokingChatClient.CurrentContext);

        var result = await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        Assert.Equal("ok", result?.ToString());
        Assert.NotNull(capture.Captured);
        Assert.Null(capture.Captured!.ConversationId);
        Assert.Equal(0, capture.Captured.TurnNumber);
        Assert.Empty(capture.Captured.History);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static async Task<ChatResponse> RunAsync(
        IServiceProvider sp, IChatClient inner, AIFunction function, string? conversationId = null)
    {
        // An empty catalog: these tests wire a hand-built function rather than a reflected tool type,
        // because the point of interest is the wrapper's dialogue with the ambient context, and a
        // lambda can act out the downstream party's part.
        var options = new ChatOptions { Tools = [function], ConversationId = conversationId }
            .WithAffiant(sp, new AffiantToolCatalog([], []));

        using var pipeline = new ChatClientBuilder(inner).UseFunctionInvocation().Build(sp);

        return await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "go")], options);
    }

    private sealed class CapturingFilter : IToolInvocationFilter
    {
        public ToolInvocationContext? Captured { get; private set; }

        public async Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            Captured = context;
            await next(context).ConfigureAwait(false);
        }
    }
}
