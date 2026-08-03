namespace Affiant.AgentFramework.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.AgentFramework.Filters;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// affiant#25 regression lock: a completion filter registered AFTER the backend bridge (a normal,
/// appended registration — not HR Portal's <c>Insert(0, ...)</c> workaround) sets
/// <c>Terminate = true</c> on its own native context before returning; the bridge must preserve
/// that decision rather than silently overwrite it with the neutral pipeline's own (here, absent)
/// Terminate verdict.
///
/// <para>
/// Drives both real bridges directly against a manually constructed native context, with no
/// Affiant <see cref="IToolInvocationFilter"/> registered in DI at all — so
/// <c>resultContext.Terminate</c> is deterministically <see langword="false"/> and the only way the
/// final <c>Terminate = true"</c> can survive is the fix under test. This isolates the bug from
/// <see cref="Affiant.Core.Filters.ReviewGateFilter"/>/<see cref="Affiant.Core.Filters.TaskInferenceMergeFilter"/>
/// entirely, matching this repo's existing "real bridge, fake continuation" pattern
/// (<c>CompletionSeamRetrySafetyTests</c>).
/// </para>
/// </summary>
public class DownstreamTerminatePreservationTests
{
    [Fact]
    public async Task SK_DownstreamFilterTerminate_SurvivesBridge_NormalAppendedRegistration()
    {
        // No IToolInvocationFilter registered — resultContext.Terminate is deterministically false;
        // the only source of Terminate=true here is the simulated downstream SK filter below.
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        var kernel = new Kernel(sp);
        var function = KernelFunctionFactory.CreateFromMethod(() => "unused", "DoTool");
        var initialResult = new FunctionResult(function, null);
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, "calling DoTool");
        var context = new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage);

        var nextCallCount = 0;

        // Simulates a host or framework IAutoFunctionInvocationFilter registered AFTER this bridge
        // in the normal (appended) DI order — its own logic decides the turn should end, and sets
        // context.Terminate = true on SK's native context before returning, exactly as a real SK
        // filter running further down the chain would.
        await bridge.OnAutoFunctionInvocationAsync(context, ctx =>
        {
            nextCallCount++;
            ctx.Terminate = true;
            return Task.CompletedTask;
        });

        Assert.Equal(1, nextCallCount);
        Assert.True(context.Terminate);
    }

    [Fact]
    public async Task SK_NoDownstreamTerminate_StaysFalse_FixDoesNotForceTerminate()
    {
        // Control: when the downstream chain does NOT set Terminate, the fix must not spuriously
        // force it true — proves the OR is genuinely conditional, not a hardcoded true.
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        var kernel = new Kernel(sp);
        var function = KernelFunctionFactory.CreateFromMethod(() => "unused", "DoTool");
        var initialResult = new FunctionResult(function, null);
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, "calling DoTool");
        var context = new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage);

        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        Assert.False(context.Terminate);
    }

    [Fact]
    public async Task MAF_DownstreamMiddlewareTerminate_SurvivesBridge()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var middleware = new AffiantFunctionInvocationMiddleware(pipeline, new StubRegistry());

        var function = AIFunctionFactory.Create(() => "unused", name: "DoTool");
        var context = new Microsoft.Extensions.AI.FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments(),
            Messages = new List<ChatMessage>(),
        };
        var stubAgent = new ChatClientAgent(new NoOpChatClient(), instructions: "stub");

        var nextCallCount = 0;

        // Simulates a host middleware chained after AffiantFunctionInvocationMiddleware via a
        // further .Use(...) call on the AIAgent builder — the real tool call/next middleware setting
        // Terminate=true before returning.
        var result = await middleware.InvokeAsync(
            stubAgent,
            context,
            (ctx, _) =>
            {
                nextCallCount++;
                ctx.Terminate = true;
                return ValueTask.FromResult<object?>("ok");
            },
            CancellationToken.None);

        Assert.Equal(1, nextCallCount);
        Assert.True(context.Terminate);
        Assert.Equal("ok", result);
    }

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        public void Register(AffiantToolDescriptor descriptor) { }
        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) => null;
        public IReadOnlyList<AffiantToolDescriptor> All => [];
    }
}
