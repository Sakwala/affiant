namespace Affiant.Extensions.AI.Tests.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Filters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Which side of the <see cref="ToolInvocationContext.NextIsToolBody"/> split this seam is on, pinned
/// as a fact rather than left to the wrapper's comment.
///
/// <para>
/// The flag exists because Semantic Kernel's completion-stage <c>next()</c> is SK's own
/// auto-invocation continuation, NOT the tool — so <see cref="ToolErrorFilter"/>'s
/// retry-once-on-retryable-failure would genuinely re-execute a write there (two independent
/// adversarial refuters reproduced it; see
/// <c>tests/Affiant.AgentFramework.Tests/Filters/CompletionSeamRetrySafetyTests.cs</c>, which pins
/// SK at exactly one call and MAF at exactly two). This adapter is MAF-shaped, and for a stronger
/// reason: <see cref="DelegatingAIFunction"/> forwards straight to the inner function with no
/// intervening continuation, so <c>next()</c> IS the tool body by construction and the flag's default
/// of <see langword="true"/> is correct here. Retrying is therefore the deliberate, documented
/// behaviour — which is only safe while that remains true of the seam.
/// </para>
///
/// <para>
/// <b>Why TimeoutException:</b> it is the one exception type
/// <c>ToolErrorFilter.MapExceptionToToolError</c> classifies as retryable
/// (<see cref="ToolErrorCodes.DbTimeout"/>) without depending on EF Core's <c>DbUpdateException</c>
/// (matched by type name at runtime, unavailable as a compile-time type here). A non-retryable
/// exception would never reach the retry gate these tests are about.
/// </para>
/// </summary>
public class CompletionSeamRetrySafetyTests
{
    [Fact]
    public async Task ExtensionsAI_WrappedFunction_PreToolFailure_RetryFires_ToolBodyCalledExactlyTwice()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        var sp = services.BuildServiceProvider();

        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());

        var toolBodyCallCount = 0;
        var inner = AIFunctionFactory.Create(
            (Func<string>)(() =>
            {
                toolBodyCallCount++;
                throw new TimeoutException("boom-retryable-at-the-wrapped-function-seam");
            }),
            name: "DoTool");

        var wrapped = new AffiantDelegatingAIFunction(inner, pipeline, new StubRegistry());

        var result = await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        // next() IS the tool body at this seam, so retry-once is both safe and expected — the same
        // number MAF's single onion produces, and deliberately not SK's completion-stage 1.
        Assert.Equal(2, toolBodyCallCount);

        var resultText = Assert.IsType<string>(result);
        using var doc = JsonDocument.Parse(resultText);
        Assert.Equal(ToolErrorCodes.DbTimeout, doc.RootElement.GetProperty("code").GetString());
        // The second failure is always surfaced as non-retryable, per ToolErrorFilter's
        // retry-exactly-once contract.
        Assert.False(doc.RootElement.GetProperty("retryable").GetBoolean());
    }

    /// <summary>
    /// The other half of retry safety: a failure raised AFTER the tool body already succeeded must
    /// never be retried, because retrying it would re-run a write that already happened.
    /// <see cref="ToolInvocationContext.ToolExecuted"/> is what closes that gate, and the wrapper sets
    /// it the instant the real call returns.
    /// </summary>
    [Fact]
    public async Task ExtensionsAI_PostToolFailure_IsNeverRetried_ToolBodyCalledExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        services.AddSingleton<IToolInvocationFilter>(new ThrowAfterToolFilter());
        var sp = services.BuildServiceProvider();

        var pipeline = new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>());

        var toolBodyCallCount = 0;
        var inner = AIFunctionFactory.Create(
            (Func<string>)(() =>
            {
                toolBodyCallCount++;
                return "written";
            }),
            name: "DoTool");

        var wrapped = new AffiantDelegatingAIFunction(inner, pipeline, new StubRegistry());

        var result = await wrapped.InvokeAsync(new AIFunctionArguments { Services = sp });

        Assert.Equal(1, toolBodyCallCount);
        // Surface-and-continue: the genuine result stands, the post-tool failure is not a tool error.
        // Compared as text because a real AIFunction's return value arrives as the JsonElement
        // AIFunctionFactory serialized it into — the shape every downstream filter reads too.
        Assert.Equal("written", result?.ToString()?.Trim('"'));
    }

    /// <summary>Retryable failure raised after the tool body has already succeeded.</summary>
    private sealed class ThrowAfterToolFilter : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            await next(context);
            throw new TimeoutException("boom-after-the-write-landed");
        }
    }

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        public void Register(AffiantToolDescriptor descriptor) { }
        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) => null;
        public IReadOnlyList<AffiantToolDescriptor> All => [];
    }
}
