namespace Affiant.Core.Tests.Filters;

using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Backend-free tests for the neutral ToolErrorFilter: exceptions become structured ToolError
/// envelopes in <see cref="ToolInvocationContext.Result"/>, retryable failures retry once,
/// successful invocations pass through, and cancellation propagates.
/// </summary>
public class ToolErrorFilterTests
{
    private static readonly IServiceProvider EmptyServices =
        new ServiceCollection().BuildServiceProvider();

    private static readonly ToolErrorFilter Filter = new(NullLogger<ToolErrorFilter>.Instance);

    private static ToolInvocationContext Ctx() => new()
    {
        FunctionName = "Fn",
        PluginName = "P",
        Arguments = new Dictionary<string, object?>(),
        Services = EmptyServices,
    };

    [Fact]
    public async Task ConvertsExceptionToStructuredError()
    {
        var ctx = Ctx();
        await Filter.OnToolInvocationAsync(ctx, _ =>
            throw new InvalidOperationException("Simulated tool failure"));

        var resultStr = ctx.Result as string ?? string.Empty;
        Assert.Contains("VALIDATION_FAILED", resultStr);
        Assert.Contains("Simulated tool failure", resultStr);
    }

    [Fact]
    public async Task SuccessfulInvocation_PassesThrough()
    {
        var ctx = Ctx();
        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = "ok"; return Task.CompletedTask; });
        Assert.Equal("ok", ctx.Result);
    }

    [Fact]
    public async Task RetryableFailure_RetriesOnce_ThenSurfacesNonRetryableEnvelope()
    {
        var ctx = Ctx();
        var calls = 0;
        await Filter.OnToolInvocationAsync(ctx, _ =>
        {
            calls++;
            throw new TimeoutException("db timed out");
        });

        Assert.Equal(2, calls); // one attempt + one retry
        var resultStr = ctx.Result as string ?? string.Empty;
        Assert.Contains("DB_TIMEOUT", resultStr);
    }

    [Fact]
    public async Task RetryableFailure_SecondAttemptSucceeds_PassesThrough()
    {
        var ctx = Ctx();
        var calls = 0;
        await Filter.OnToolInvocationAsync(ctx, c =>
        {
            calls++;
            if (calls == 1) throw new TimeoutException("db timed out");
            c.Result = "recovered";
            return Task.CompletedTask;
        });

        Assert.Equal(2, calls);
        Assert.Equal("recovered", ctx.Result);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var ctx = Ctx();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Filter.OnToolInvocationAsync(ctx, _ => throw new OperationCanceledException()));
    }
}
