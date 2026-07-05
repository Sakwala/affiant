namespace Affiant.Core.Tests.Filters;

using System.Diagnostics;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Verifies ToolTracingFilter's span contract: operation name, source name, tag values,
/// and error-path behaviour (status set, exception re-thrown, span disposed in finally).
/// Backend-free — drives the neutral <see cref="ToolInvocationContext"/> directly.
/// </summary>
public sealed class ToolTracingFilterTests
{
    private static readonly IServiceProvider EmptyServices =
        new ServiceCollection().BuildServiceProvider();

    private static ActivityListener CreateListener(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Affiant.Framework",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => stopped.Add(a),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static ToolInvocationContext Ctx(string functionName) => new()
    {
        FunctionName = functionName,
        PluginName = string.Empty,
        Arguments = new Dictionary<string, object?>(),
        Services = EmptyServices,
    };

    private static Task Run(string functionName, Func<ToolInvocationContext, Task> next) =>
        new ToolTracingFilter().OnToolInvocationAsync(Ctx(functionName), next);

    // ── span structure ───────────────────────────────────────────────────────

    [Fact]
    public async Task Span_OperationName_IsExecuteTool_WithAffiantFrameworkSource()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        await Run("Probe1", ctx => { ctx.Result = "ok"; return Task.CompletedTask; });

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("Affiant.Framework", span.Source.Name);
    }

    [Fact]
    public async Task Tag_GenAiToolName_CarriesFunctionName()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        await Run("NamedFunction", ctx => { ctx.Result = "ok"; return Task.CompletedTask; });

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("NamedFunction", span.GetTagItem("gen_ai.tool.name"));
    }

    // ── tool_status values ───────────────────────────────────────────────────

    [Fact]
    public async Task ToolStatus_IsOk_WhenResultIsNonEmpty()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        await Run("OkFn", ctx => { ctx.Result = "non-empty"; return Task.CompletedTask; });

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("ok", span.GetTagItem("tool_status"));
    }

    [Fact]
    public async Task ToolStatus_IsEmpty_WhenResultIsNull()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        await Run("NullFn", ctx => { ctx.Result = null; return Task.CompletedTask; });

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("empty", span.GetTagItem("tool_status"));
    }

    // ── error path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OnToolThrow_ToolStatusIsError_AndActivityStatusIsError()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run("ThrowFn", _ => throw new InvalidOperationException("boom")));

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("error", span.GetTagItem("tool_status"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task OnToolThrow_SpanIsStoppedViaFinally_BeforeExceptionPropagates()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run("ThrowFn2", _ => throw new InvalidOperationException("boom")));

        // ActivityStopped fires only when the activity is stopped (disposed via the finally block).
        Assert.Single(stopped, a => a.OperationName == "execute_tool");
    }
}
