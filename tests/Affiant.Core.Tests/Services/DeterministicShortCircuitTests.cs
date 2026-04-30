namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Services;
using Microsoft.SemanticKernel;
using Xunit;

/// <summary>
/// Uses a real Kernel to exercise DeterministicShortCircuit through the SK filter pipeline.
/// FunctionInvocationContext has no public constructor so we test via kernel invocation.
/// </summary>
public class DeterministicShortCircuitTests
{
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

    private static Kernel BuildKernel(DeterministicShortCircuit filter)
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.FunctionInvocationFilters.Add(filter);
        return kernel;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoInterceptors_KernelInvocationProceeds()
    {
        var filter = new DeterministicShortCircuit([]);
        var kernel = BuildKernel(filter);

        var originalCalled = false;
        var function = KernelFunctionFactory.CreateFromMethod(
            () => { originalCalled = true; return "original"; },
            functionName: "TestFunction");

        var result = await kernel.InvokeAsync(function);

        Assert.True(originalCalled);
        Assert.Equal("original", result.GetValue<string>());
    }

    [Fact]
    public async Task OneMatchingInterceptor_LlmInvocationSkipped()
    {
        var interceptor = new AlwaysMatchingInterceptor("I cannot do this");
        var filter = new DeterministicShortCircuit([interceptor]);
        var kernel = BuildKernel(filter);

        var originalCalled = false;
        var function = KernelFunctionFactory.CreateFromMethod(
            () => { originalCalled = true; return "original"; },
            functionName: "TestFunction");

        var result = await kernel.InvokeAsync(function);

        Assert.Equal(1, interceptor.MatchCallCount);
        Assert.Equal(1, interceptor.HandleCallCount);
        Assert.Equal("I cannot do this", result.GetValue<string>());
        Assert.False(originalCalled);
    }

    [Fact]
    public async Task OneNonMatchingInterceptor_KernelInvocationProceeds()
    {
        var interceptor = new NeverMatchingInterceptor();
        var filter = new DeterministicShortCircuit([interceptor]);
        var kernel = BuildKernel(filter);

        var originalCalled = false;
        var function = KernelFunctionFactory.CreateFromMethod(
            () => { originalCalled = true; return "original"; },
            functionName: "TestFunction");

        var result = await kernel.InvokeAsync(function);

        Assert.Equal(1, interceptor.MatchCallCount);
        Assert.Equal(0, interceptor.HandleCallCount);
        Assert.True(originalCalled);
        Assert.Equal("original", result.GetValue<string>());
    }

    [Fact]
    public async Task MultipleInterceptors_FirstMatchWins()
    {
        var interceptorA = new NeverMatchingInterceptor();
        var interceptorB = new AlwaysMatchingInterceptor("handled by B");
        var interceptorC = new AlwaysMatchingInterceptor("handled by C");
        var filter = new DeterministicShortCircuit([interceptorA, interceptorB, interceptorC]);
        var kernel = BuildKernel(filter);

        var originalCalled = false;
        var function = KernelFunctionFactory.CreateFromMethod(
            () => { originalCalled = true; return "original"; },
            functionName: "TestFunction");

        var result = await kernel.InvokeAsync(function);

        Assert.Equal(1, interceptorA.MatchCallCount);
        Assert.Equal(1, interceptorB.MatchCallCount);
        Assert.Equal(1, interceptorB.HandleCallCount);
        Assert.Equal(0, interceptorC.MatchCallCount);
        Assert.Equal("handled by B", result.GetValue<string>());
        Assert.False(originalCalled);
    }

    [Fact]
    public async Task RespectsCancellationToken()
    {
        var interceptor = new AlwaysMatchingInterceptor("result");
        var filter = new DeterministicShortCircuit([interceptor]);
        var kernel = BuildKernel(filter);

        var function = KernelFunctionFactory.CreateFromMethod(
            () => "original",
            functionName: "TestFunction");

        using var cts = new CancellationTokenSource();
        await kernel.InvokeAsync(function, cancellationToken: cts.Token);

        Assert.Equal(cts.Token, interceptor.CapturedMatchToken);
        Assert.Equal(cts.Token, interceptor.CapturedHandleToken);
    }
}
