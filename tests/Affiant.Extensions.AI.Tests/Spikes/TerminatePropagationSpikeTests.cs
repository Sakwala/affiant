namespace Affiant.Extensions.AI.Tests.Spikes;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Xunit;

/// <summary>
/// The mandatory spike from the Affiant.Extensions.AI design brief
/// (<c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>, decision 2).
///
/// WHAT IS BEING PROVEN, AND WHY IT WAS IN DOUBT
/// ---------------------------------------------
/// The whole Affiant.Extensions.AI adapter rests on one unverified claim from the seam probe
/// (<c>docs/overnight-mission-2026-08-20/research/meai-seam-probe.md</c> §1.4/§5): that when an
/// <see cref="AIFunction"/> is wrapped in a <see cref="DelegatingAIFunction"/> (the mechanism the
/// Microsoft Agent Framework itself uses for its function-invocation middleware) and that wrapper
/// mutates the ambient <see cref="FunctionInvokingChatClient.CurrentContext"/> from inside
/// <c>InvokeCoreAsync</c>, the mutation is seen by the OUTER
/// <c>FunctionInvokingChatClient.ProcessFunctionCallsAsync</c> loop — specifically its
/// <c>results.Exists(r =&gt; r.Terminate)</c> check — so the tool loop actually stops and the caller
/// sees the wrapper's replaced result rather than the tool's real one.
///
/// The probe established this by reading source only (same object reference, AsyncLocal flows
/// across await without a thread-pool hop) and explicitly refused to call it verified because it
/// executed no code. Affiant's review gate (<c>Affiant.Core.Filters.ReviewGateFilter</c>) needs
/// exactly this power: replace the tool result with a turn-ending message and stop the loop, from
/// inside the same call that ran the tool. If it does not propagate, the adapter cannot be built
/// on this seam at all.
///
/// FAIL-FIRST DEMONSTRATION
/// ------------------------
/// <see cref="Baseline_UnwrappedFunction_LoopContinuesAndCallerSeesTheRealResult"/> runs the exact
/// same harness with the plain, unwrapped <see cref="AIFunction"/>. It pins the behaviour the spike
/// must differ from: the loop does NOT stop (the fake client is asked for a second completion) and
/// the caller sees the tool's genuine result. Every assertion in
/// <see cref="Wrapped_MutatingCurrentContext_StopsTheLoopAndReplacesTheResult"/> fails when run
/// against that baseline wiring — which is how this spike was demonstrated fail-first before the
/// wrapper's mutation was written.
///
/// Verified against Microsoft.Extensions.AI 10.9.0 on 2026-08-20.
/// </summary>
public sealed class TerminatePropagationSpikeTests
{
    private const string ToolName = "record_decision";
    private const string RealToolResult = "REAL-TOOL-RESULT";
    private const string ReplacedResult = "REPLACED-BY-AFFIANT";
    private const string SecondTurnText = "SECOND-TURN-TEXT";

    /// <summary>
    /// Baseline. No wrapper, no <c>Terminate</c> mutation: <see cref="FunctionInvokingChatClient"/>
    /// runs the tool, appends its genuine result, and goes back to the model for another completion.
    /// </summary>
    [Fact]
    public async Task Baseline_UnwrappedFunctionLoopContinuesAndCallerSeesTheRealResult()
    {
        var toolCalls = 0;
        var tool = AIFunctionFactory.Create(
            () => { toolCalls++; return RealToolResult; }, ToolName);

        var (response, client) = await RunLoopAsync(tool);

        // The loop did NOT stop: the fake client was asked for a second completion after the tool ran.
        Assert.Equal(2, client.CallCount);
        Assert.Equal(1, toolCalls);
        Assert.Equal(SecondTurnText, response.Text);

        // The caller sees the tool's genuine result on the wire.
        Assert.Equal(RealToolResult, SingleFunctionResult(response));
    }

    /// <summary>
    /// The spike. A <see cref="DelegatingAIFunction"/> wrapper reads the ambient
    /// <see cref="FunctionInvokingChatClient.CurrentContext"/>, sets <c>Terminate = true</c> and
    /// returns a substituted result — and both must reach the outer loop.
    /// </summary>
    [Fact]
    public async Task WrappedMutatingCurrentContextStopsTheLoopAndReplacesTheResult()
    {
        var toolCalls = 0;
        var inner = AIFunctionFactory.Create(
            () => { toolCalls++; return RealToolResult; }, ToolName);

        FunctionInvocationContext? observed = null;
        var wrapped = new TerminatingProbeFunction(inner, ctx =>
        {
            observed = ctx;
            ctx.Terminate = true;
        });

        var (response, client) = await RunLoopAsync(wrapped);

        // 1. The wrapper genuinely saw the ambient context FunctionInvokingChatClient populated —
        //    not a fabricated fallback. This is the AsyncLocal carrier the probe identified as the
        //    M.E.AI analog of Semantic Kernel's Kernel.Data.
        Assert.NotNull(observed);
        Assert.Equal(ToolName, observed!.Function.Name);
        Assert.Same(wrapped, observed.Function);

        // 2. The real tool still ran exactly once (the wrapper delegates to it, it does not shadow it).
        Assert.Equal(1, toolCalls);

        // 3. TERMINATE PROPAGATED: the loop stopped. The fake client was asked for exactly ONE
        //    completion — the second, post-tool completion the baseline shows never happened.
        Assert.Equal(1, client.CallCount);
        Assert.NotEqual(SecondTurnText, response.Text);

        // 4. RESULT REPLACEMENT PROPAGATED: the caller sees the wrapper's substituted result, not
        //    the tool's genuine one. This is the exact power ReviewGateFilter needs to turn a
        //    write-proposal into a turn-ending "queued for review" message.
        Assert.Equal(ReplacedResult, SingleFunctionResult(response));
        Assert.DoesNotContain(RealToolResult, SingleFunctionResult(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// Termination composes rather than clobbers: a wrapper that leaves <c>Terminate</c> alone must
    /// not stop the loop, so an Affiant bridge's OR-not-overwrite composition (affiant#25) has a
    /// meaningful "false" to OR against at this seam.
    /// </summary>
    [Fact]
    public async Task WrappedWithoutMutatingTerminateLeavesTheLoopRunning()
    {
        var inner = AIFunctionFactory.Create(() => RealToolResult, ToolName);
        var wrapped = new TerminatingProbeFunction(inner, _ => { });

        var (response, client) = await RunLoopAsync(wrapped);

        Assert.Equal(2, client.CallCount);
        Assert.Equal(SecondTurnText, response.Text);
        // The result is still replaced — replacement is the wrapper's return value, independent of
        // Terminate — so the two powers are separable at this seam.
        Assert.Equal(ReplacedResult, SingleFunctionResult(response));
    }

    private static async Task<(ChatResponse Response, CountingScriptedChatClient Client)> RunLoopAsync(AITool tool)
    {
        var client = new CountingScriptedChatClient(ToolName, SecondTurnText);
        using var pipeline = new ChatClientBuilder(client).UseFunctionInvocation().Build();

        var response = await pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "please record the decision")],
            new ChatOptions { Tools = [tool] });

        return (response, client);
    }

    private static string SingleFunctionResult(ChatResponse response)
    {
        var result = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        return result.Result?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// The probe wrapper. Shaped exactly like MAF's own private <c>MiddlewareEnabledFunction</c>
    /// (agent-framework <c>FunctionInvocationDelegatingAgent.cs</c>): read
    /// <see cref="FunctionInvokingChatClient.CurrentContext"/>, act on it, delegate to the inner
    /// function, and return a value of the wrapper's choosing — but with no
    /// <c>Microsoft.Agents.AI</c> types involved anywhere.
    /// </summary>
    private sealed class TerminatingProbeFunction(AIFunction inner, Action<FunctionInvocationContext> onContext)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var context = FunctionInvokingChatClient.CurrentContext;
            if (context is not null)
                onContext(context);

            // base.InvokeCoreAsync delegates to the inner (real) function — the "next() is the tool
            // body" property the seam probe verified from FunctionInvokingChatClient source.
            _ = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);

            return ReplacedResult;
        }
    }

    /// <summary>
    /// Fake <see cref="IChatClient"/>: first completion requests the tool call, every later
    /// completion returns plain text. <see cref="CallCount"/> is the loop-continuation witness —
    /// it reaches 2 only if <see cref="FunctionInvokingChatClient"/> went back to the model after
    /// the tool ran.
    /// </summary>
    private sealed class CountingScriptedChatClient(string functionName, string finalText) : IChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _callCount);

            var message = count == 1
                ? new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-spike-1", functionName, new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, finalText);

            return Task.FromResult(new ChatResponse(message));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
                yield return new ChatResponseUpdate(message.Role, message.Contents);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
