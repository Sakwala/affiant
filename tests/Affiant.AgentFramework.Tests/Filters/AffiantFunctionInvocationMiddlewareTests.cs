namespace Affiant.AgentFramework.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.AgentFramework.Filters;
using Affiant.AgentFramework.Tests.Utilities;
using Affiant.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Unit tests for <see cref="AffiantFunctionInvocationMiddleware"/> driven directly against a
/// manually constructed <see cref="FunctionInvocationContext"/> — no real agent round trip.
/// Covers evidence sealing by return value (proposal §2), terminate mapping, and plugin-name
/// resolution via <see cref="IAffiantToolRegistry"/>.
/// </summary>
public class AffiantFunctionInvocationMiddlewareTests
{
    private static readonly AIAgent StubAgent = new ChatClientAgent(new NoOpChatClient(), instructions: "stub");

    private static ToolInvocationPipeline Pipeline(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>());

    private static FunctionInvocationContext BuildContext(AIFunction function, object? initialArgValue = null)
    {
        var arguments = new AIFunctionArguments();
        if (initialArgValue is not null) arguments["x"] = initialArgValue;

        return new FunctionInvocationContext
        {
            Function = function,
            Arguments = arguments,
            Messages = new List<ChatMessage>(),
        };
    }

    private static AIFunction MakeFunction(string name, Func<string> body) =>
        AIFunctionFactory.Create(body, name: name);

    // ── Sealing by return value ─────────────────────────────────────────────

    [Fact]
    public async Task NoFilterReplacesResult_ReturnsToolsRawValue()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("Passthrough", () => "raw");
        var context = BuildContext(function);

        var result = await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal("raw", result);
    }

    [Fact]
    public async Task FilterReplacesResult_ReturnedValueIsTheReplacement()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new ReplacingFilter("replaced"));
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("Replaced", () => "raw");
        var context = BuildContext(function);

        var result = await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal("replaced", result);
    }

    // ── Terminate mapping ────────────────────────────────────────────────────

    [Fact]
    public async Task FilterSetsTerminate_MapsOntoFunctionInvocationContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolInvocationFilter>(new TerminatingFilter());
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("Terminating", () => "raw");
        var context = BuildContext(function);
        Assert.False(context.Terminate);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.True(context.Terminate);
    }

    [Fact]
    public async Task NoFilterSetsTerminate_StaysFalse()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("NonTerminating", () => "raw");
        var context = BuildContext(function);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.False(context.Terminate);
    }

    // ── Plugin-name resolution via registry ─────────────────────────────────

    [Fact]
    public async Task PluginName_ResolvedFromRegistry_ByFunctionName()
    {
        var services = new ServiceCollection();
        var observed = new List<string>();
        services.AddSingleton<IToolInvocationFilter>(new RecordingFilter(observed));
        var sp = services.BuildServiceProvider();

        var registry = new StubRegistry();
        registry.Register(new AffiantToolDescriptor("Widgets_Create", "WidgetPlugin", Operation.WriteCreate, "Widget", null));

        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), registry);
        var function = MakeFunction("Widgets_Create", () => "raw");
        var context = BuildContext(function);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal(["WidgetPlugin"], observed);
    }

    [Fact]
    public async Task PluginName_EmptyString_WhenNoDescriptorRegistered()
    {
        var services = new ServiceCollection();
        var observed = new List<string>();
        services.AddSingleton<IToolInvocationFilter>(new RecordingFilter(observed));
        var sp = services.BuildServiceProvider();

        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());
        var function = MakeFunction("Unregistered", () => "raw");
        var context = BuildContext(function);

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        Assert.Equal([""], observed);
    }

    // ── Arguments shared by reference ────────────────────────────────────────

    [Fact]
    public async Task Arguments_SharedByReference_WithMafArguments()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var middleware = new AffiantFunctionInvocationMiddleware(Pipeline(sp), new StubRegistry());

        var function = MakeFunction("EchoArgs", () => "raw");
        var context = BuildContext(function, initialArgValue: "hello");

        await middleware.InvokeAsync(
            StubAgent, context, (_, _) => new ValueTask<object?>("raw"), CancellationToken.None);

        // The neutral pipeline read the same AIFunctionArguments instance MAF supplied.
        Assert.Equal("hello", context.Arguments["x"]);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class StubRegistry : IAffiantToolRegistry
    {
        private readonly List<AffiantToolDescriptor> _descriptors = [];
        public void Register(AffiantToolDescriptor descriptor) => _descriptors.Add(descriptor);

        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) =>
            _descriptors.FirstOrDefault(d => d.FunctionName == functionName
                && (pluginName is null || d.PluginName == pluginName));

        public IReadOnlyList<AffiantToolDescriptor> All => _descriptors;
    }

    private sealed class ReplacingFilter(object replacement) : IToolInvocationFilter
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

    private sealed class RecordingFilter(List<string> observed) : IToolInvocationFilter
    {
        public Task OnToolInvocationAsync(ToolInvocationContext context, Func<ToolInvocationContext, Task> next, CancellationToken cancellationToken = default)
        {
            observed.Add(context.PluginName);
            return next(context);
        }
    }
}
