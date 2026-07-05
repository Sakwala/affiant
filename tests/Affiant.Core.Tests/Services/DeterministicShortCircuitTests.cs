namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Exercises DeterministicShortCircuit against the neutral <see cref="ToolInvocationContext"/>.
/// Backend-free — invokes the filter directly with a terminal delegate standing in for the tool.
/// </summary>
public class DeterministicShortCircuitTests
{
    private static readonly IServiceProvider EmptyServices =
        new ServiceCollection().BuildServiceProvider();

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class AlwaysMatchingInterceptor(object? response) : IIntentInterceptor
    {
        public int MatchCallCount { get; private set; }
        public int HandleCallCount { get; private set; }
        public CancellationToken CapturedMatchToken { get; private set; }
        public CancellationToken CapturedHandleToken { get; private set; }

        public Task<bool> MatchesAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            MatchCallCount++;
            CapturedMatchToken = cancellationToken;
            return Task.FromResult(true);
        }

        public Task<object?> HandleAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            HandleCallCount++;
            CapturedHandleToken = cancellationToken;
            return Task.FromResult(response);
        }
    }

    private sealed class NeverMatchingInterceptor : IIntentInterceptor
    {
        public int MatchCallCount { get; private set; }
        public int HandleCallCount { get; private set; }

        public Task<bool> MatchesAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            MatchCallCount++;
            return Task.FromResult(false);
        }

        public Task<object?> HandleAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            HandleCallCount++;
            return Task.FromResult<object?>(null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ToolInvocationContext Ctx() => new()
    {
        FunctionName = "TestFunction",
        PluginName = string.Empty,
        Arguments = new Dictionary<string, object?>(),
        Services = EmptyServices,
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoInterceptors_ToolInvocationProceeds()
    {
        var filter = new DeterministicShortCircuit([]);
        var ctx = Ctx();

        var originalCalled = false;
        await filter.OnToolInvocationAsync(ctx, c =>
        {
            originalCalled = true;
            c.Result = "original";
            return Task.CompletedTask;
        });

        Assert.True(originalCalled);
        Assert.Equal("original", ctx.Result);
    }

    [Fact]
    public async Task OneMatchingInterceptor_ToolInvocationSkipped()
    {
        var interceptor = new AlwaysMatchingInterceptor("I cannot do this");
        var filter = new DeterministicShortCircuit([interceptor]);
        var ctx = Ctx();

        var originalCalled = false;
        await filter.OnToolInvocationAsync(ctx, c =>
        {
            originalCalled = true;
            c.Result = "original";
            return Task.CompletedTask;
        });

        Assert.Equal(1, interceptor.MatchCallCount);
        Assert.Equal(1, interceptor.HandleCallCount);
        Assert.Equal("I cannot do this", ctx.Result);
        Assert.False(originalCalled);
    }

    [Fact]
    public async Task OneNonMatchingInterceptor_ToolInvocationProceeds()
    {
        var interceptor = new NeverMatchingInterceptor();
        var filter = new DeterministicShortCircuit([interceptor]);
        var ctx = Ctx();

        var originalCalled = false;
        await filter.OnToolInvocationAsync(ctx, c =>
        {
            originalCalled = true;
            c.Result = "original";
            return Task.CompletedTask;
        });

        Assert.Equal(1, interceptor.MatchCallCount);
        Assert.Equal(0, interceptor.HandleCallCount);
        Assert.True(originalCalled);
        Assert.Equal("original", ctx.Result);
    }

    [Fact]
    public async Task MultipleInterceptors_FirstMatchWins()
    {
        var interceptorA = new NeverMatchingInterceptor();
        var interceptorB = new AlwaysMatchingInterceptor("handled by B");
        var interceptorC = new AlwaysMatchingInterceptor("handled by C");
        var filter = new DeterministicShortCircuit([interceptorA, interceptorB, interceptorC]);
        var ctx = Ctx();

        var originalCalled = false;
        await filter.OnToolInvocationAsync(ctx, c =>
        {
            originalCalled = true;
            c.Result = "original";
            return Task.CompletedTask;
        });

        Assert.Equal(1, interceptorA.MatchCallCount);
        Assert.Equal(1, interceptorB.MatchCallCount);
        Assert.Equal(1, interceptorB.HandleCallCount);
        Assert.Equal(0, interceptorC.MatchCallCount);
        Assert.Equal("handled by B", ctx.Result);
        Assert.False(originalCalled);
    }

    [Fact]
    public async Task RespectsCancellationToken()
    {
        var interceptor = new AlwaysMatchingInterceptor("result");
        var filter = new DeterministicShortCircuit([interceptor]);
        var ctx = Ctx();

        using var cts = new CancellationTokenSource();
        await filter.OnToolInvocationAsync(ctx, _ => Task.CompletedTask, cts.Token);

        Assert.Equal(cts.Token, interceptor.CapturedMatchToken);
        Assert.Equal(cts.Token, interceptor.CapturedHandleToken);
    }
}
