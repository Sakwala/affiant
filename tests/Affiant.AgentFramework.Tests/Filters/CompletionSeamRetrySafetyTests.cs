namespace Affiant.AgentFramework.Tests.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.AgentFramework.Filters;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Area-3 P2 FIX-ROUND regression lock (promotes two independent adversarial refuters' probes):
/// promotes <see cref="Affiant.Abstractions.Models.ToolInvocationContext.NextIsToolBody"/> — the
/// fix for the completion-seam retry double-fire finding (SK's completion-stage <c>next()</c> is
/// SK's own auto-invocation continuation, NOT the tool; a naive
/// <c>!ToolExecuted</c>-only retry gate would call that continuation twice for a pre-tool-style
/// failure, genuinely re-executing the tool — both refuters reproduced <c>nextCallCount == 2</c>
/// against the ruling-1 implementation before this fix).
///
/// <para>
/// <b>Why TimeoutException:</b> it is the one exception type <c>ToolErrorFilter.MapExceptionToToolError</c>
/// classifies as retryable (<see cref="ToolErrorCodes.DbTimeout"/>) without depending on
/// EF Core's <c>DbUpdateException</c> (checked by type name at runtime, unavailable as a compile-time
/// type in this test project) — the classification MUST be retryable for these tests to exercise
/// the retry-gate decision at all; a non-retryable exception would never reach the branch under test.
/// </para>
/// </summary>
public class CompletionSeamRetrySafetyTests
{
    [Fact]
    public async Task SK_CompletionStage_PreToolFailure_NextCalledExactlyOnce_NoDoubleExecution()
    {
        // Real AffiantAutoFunctionInvocationBridge, no other filters registered — so ToolErrorFilter
        // sees the injected failure directly at the completion-stage seam and next(context) below IS
        // the (fake) SK continuation the bridge nested-invokes.
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        var sp = services.BuildServiceProvider();

        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        var kernel = new Kernel(sp);
        var function = KernelFunctionFactory.CreateFromMethod(() => "unused", "DoTool");
        var initialResult = new FunctionResult(function, null);
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, "calling DoTool");
        var context = new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage);

        var nextCallCount = 0;

        // Simulates a host-registered SK filter (or SK's own argument binding) throwing BEFORE the
        // nested invocation-stage call ever reaches the real tool — the exact scenario both
        // refuters used. ToolExecuted stays false because the tool never ran.
        await bridge.OnAutoFunctionInvocationAsync(context, _ =>
        {
            nextCallCount++;
            throw new TimeoutException("boom-pre-tool-at-completion-seam");
        });

        // The fix: NextIsToolBody=false at this seam blocks the retry entirely — next() must be
        // called exactly once, never twice (pre-fix, both refuters observed exactly 2).
        Assert.Equal(1, nextCallCount);

        // Converted to a typed ToolError exactly as usual (the one-failure-contract still holds —
        // SK's auto-invoke loop must never see a raw exception) — just never retried.
        var resultText = context.Result.GetValue<object>() as string;
        Assert.NotNull(resultText);
        using var doc = JsonDocument.Parse(resultText!);
        Assert.Equal(ToolErrorCodes.DbTimeout, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MAF_SingleOnion_PreToolFailure_RetryStillFires_NextCalledExactlyTwice()
    {
        // Control: MAF's single onion has no completion-seam split — next() genuinely IS the tool
        // (or leads directly to it), so NextIsToolBody stays at its default true and the existing
        // retry-once behavior is unchanged and correct. This asymmetry (SK: 1 call, MAF: 2 calls)
        // is now deliberate and documented, not a leftover inconsistency.
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
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

        var result = await middleware.InvokeAsync(
            stubAgent,
            context,
            (_, _) =>
            {
                nextCallCount++;
                throw new TimeoutException("boom-pre-tool-single-onion");
            },
            CancellationToken.None);

        // MAF's next() IS the tool body at this seam by construction — retrying it is exactly the
        // documented, tested, deliberate behavior (asymmetric with SK on purpose).
        Assert.Equal(2, nextCallCount);

        var resultText = result as string;
        Assert.NotNull(resultText);
        using var doc = JsonDocument.Parse(resultText!);
        Assert.Equal(ToolErrorCodes.DbTimeout, doc.RootElement.GetProperty("code").GetString());
        // Second failure is always surfaced as non-retryable per ToolErrorFilter's existing
        // retry-exactly-once contract — unaffected by this fix.
        Assert.False(doc.RootElement.GetProperty("retryable").GetBoolean());
    }

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        public void Register(AffiantToolDescriptor descriptor) { }
        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) => null;
        public IReadOnlyList<AffiantToolDescriptor> All => [];
    }
}
