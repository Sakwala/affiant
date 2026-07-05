namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Backend-free tests for the neutral ToolInvocationPipeline runner: canonical onion order,
/// result replacement, terminate propagation, per-invocation DI scope lifetime, and error
/// enveloping via the neutral ToolErrorFilter.
/// </summary>
public class ToolInvocationPipelineTests
{
    private static ToolInvocationRequest Request() =>
        new("Fn", "P", new Dictionary<string, object?>());

    private static IReadOnlyList<IToolInvocationFilter> All(IReadOnlyList<IToolInvocationFilter> f) => f;

    private static ToolInvocationPipeline Pipeline(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>());

    // ── Onion order ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filters_RunAsOnion_InRegistrationOrder()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new LoggingFilter("A", log));
        services.AddSingleton<IToolInvocationFilter>(new LoggingFilter("B", log));
        services.AddSingleton<IToolInvocationFilter>(new LoggingFilter("C", log));
        var sp = services.BuildServiceProvider();

        await Pipeline(sp).RunAsync(Request(), All, _ => { log.Add("tool"); return Task.CompletedTask; });

        Assert.Equal(
            ["pre-A", "pre-B", "pre-C", "tool", "post-C", "post-B", "post-A"],
            log);
    }

    // ── Result replacement ──────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_CanReplaceResult_AfterNext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ResultReplacingFilter("wrapped"));
        var sp = services.BuildServiceProvider();

        var ctx = await Pipeline(sp).RunAsync(
            Request(), All, c => { c.Result = "raw"; return Task.CompletedTask; });

        Assert.Equal("wrapped", ctx.Result);
    }

    // ── Terminate propagation ───────────────────────────────────────────────────

    [Fact]
    public async Task Filter_CanSetTerminate_AndRunnerReturnsIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new TerminatingFilter());
        var sp = services.BuildServiceProvider();

        var ctx = await Pipeline(sp).RunAsync(Request(), All, _ => Task.CompletedTask);

        Assert.True(ctx.Terminate);
    }

    // ── Scope lifetime ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EachInvocation_GetsFreshScope_AndDisposesIt()
    {
        var services = new ServiceCollection();
        services.AddScoped<DisposableMarker>();
        var captured = new List<DisposableMarker>();
        services.AddSingleton<IToolInvocationFilter>(new ScopeCapturingFilter(captured));
        var sp = services.BuildServiceProvider();
        var pipeline = Pipeline(sp);

        await pipeline.RunAsync(Request(), All, _ => Task.CompletedTask);
        await pipeline.RunAsync(Request(), All, _ => Task.CompletedTask);

        Assert.Equal(2, captured.Count);
        Assert.NotSame(captured[0], captured[1]);      // fresh scope per invocation
        Assert.True(captured[0].Disposed);             // scope disposed after invocation
        Assert.True(captured[1].Disposed);
    }

    // ── Error enveloping ────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolErrorFilter_InPipeline_EnvelopesThrownException()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ToolErrorFilter(NullLogger<ToolErrorFilter>.Instance));
        var sp = services.BuildServiceProvider();

        var ctx = await Pipeline(sp).RunAsync(
            Request(), All, _ => throw new InvalidOperationException("boom"));

        var result = ctx.Result as string ?? string.Empty;
        Assert.Contains("VALIDATION_FAILED", result);
        Assert.Contains("boom", result);
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    private sealed class LoggingFilter(string name, List<string> log) : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            log.Add($"pre-{name}");
            await next(context);
            log.Add($"post-{name}");
        }
    }

    private sealed class ResultReplacingFilter(object replacement) : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            await next(context);
            context.Result = replacement;
        }
    }

    private sealed class TerminatingFilter : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            await next(context);
            context.Terminate = true;
        }
    }

    private sealed class ScopeCapturingFilter(List<DisposableMarker> captured) : IToolInvocationFilter
    {
        public Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            captured.Add(context.Services.GetRequiredService<DisposableMarker>());
            return next(context);
        }
    }

    private sealed class DisposableMarker : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
