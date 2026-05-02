namespace Affiant.Core.Tests.Filters;

using System.Diagnostics;
using Affiant.Core.Filters;
using Microsoft.SemanticKernel;
using Xunit;

/// <summary>
/// Verifies ToolTracingFilter's span contract: operation name, scope name, tag values,
/// and error-path behaviour (status set, exception re-thrown, span disposed in finally).
/// </summary>
public sealed class ToolTracingFilterTests
{
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

    private static Kernel BuildKernelWithFilter()
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.FunctionInvocationFilters.Add(new ToolTracingFilter());
        return kernel;
    }

    // ── span structure ───────────────────────────────────────────────────────

    [Fact]
    public async Task Span_OperationName_IsExecuteTool_WithAffiantFrameworkSource()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        var kernel = BuildKernelWithFilter();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("P",
            [KernelFunctionFactory.CreateFromMethod(() => "ok", "Probe1")]));

        await kernel.InvokeAsync("P", "Probe1");

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("Affiant.Framework", span.Source.Name);
    }

    [Fact]
    public async Task Tag_GenAiToolName_CarriesKernelFunctionName()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        var kernel = BuildKernelWithFilter();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("P",
            [KernelFunctionFactory.CreateFromMethod(() => "ok", "NamedFunction")]));

        await kernel.InvokeAsync("P", "NamedFunction");

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("NamedFunction", span.GetTagItem("gen_ai.tool.name"));
    }

    // ── tool_status values ───────────────────────────────────────────────────

    [Fact]
    public async Task ToolStatus_IsOk_WhenResultIsNonEmpty()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        var kernel = BuildKernelWithFilter();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("P",
            [KernelFunctionFactory.CreateFromMethod(() => "non-empty", "OkFn")]));

        await kernel.InvokeAsync("P", "OkFn");

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("ok", span.GetTagItem("tool_status"));
    }

    [Fact]
    public async Task ToolStatus_IsEmpty_WhenResultIsNull()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        var kernel = BuildKernelWithFilter();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("P",
            [KernelFunctionFactory.CreateFromMethod((Func<string?>)(() => null), "NullFn")]));

        await kernel.InvokeAsync("P", "NullFn");

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("empty", span.GetTagItem("tool_status"));
    }

    // ── error path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPluginThrow_ToolStatusIsError_AndActivityStatusIsError()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        var kernel = BuildKernelWithFilter();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("P",
            [KernelFunctionFactory.CreateFromMethod(
                (Func<string>)(() => throw new InvalidOperationException("boom")),
                "ThrowFn")]));

        await Assert.ThrowsAnyAsync<Exception>(() => kernel.InvokeAsync("P", "ThrowFn"));

        var span = stopped.Single(a => a.OperationName == "execute_tool");
        Assert.Equal("error", span.GetTagItem("tool_status"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task OnPluginThrow_SpanIsStoppedViaFinally_BeforeExceptionPropagates()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        var kernel = BuildKernelWithFilter();
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("P",
            [KernelFunctionFactory.CreateFromMethod(
                (Func<string>)(() => throw new InvalidOperationException("boom")),
                "ThrowFn2")]));

        await Assert.ThrowsAnyAsync<Exception>(() => kernel.InvokeAsync("P", "ThrowFn2"));

        // ActivityStopped fires only when the activity is stopped (disposed via the finally block).
        // If the finally block did not run, this assertion would fail.
        Assert.Single(stopped, a => a.OperationName == "execute_tool");
    }
}
