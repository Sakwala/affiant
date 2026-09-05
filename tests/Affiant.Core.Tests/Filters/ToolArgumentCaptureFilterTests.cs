namespace Affiant.Core.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Backend-free unit tests for the neutral ToolArgumentCaptureFilter.
/// Verifies that a model's tool arguments reach the IContextFabric as the values it PROPOSES and
/// nothing more (PV-1: an argument is not evidence about where a value came from), that
/// unregistered tools are skipped, and that next() always fires.
/// </summary>
public class ToolArgumentCaptureFilterTests
{
    private static readonly IServiceProvider EmptyServices =
        new ServiceCollection().BuildServiceProvider();

    private static ToolArgumentCaptureFilter BuildFilter(IContextFabric fabric, IAffiantToolRegistry registry)
        => new(fabric, registry, NullLogger<ToolArgumentCaptureFilter>.Instance);

    private static ToolInvocationContext Ctx(IDictionary<string, object?> args) => new()
    {
        FunctionName = "CaptureFn",
        PluginName = "TestPlugin",
        Arguments = args,
        Services = EmptyServices,
    };

    private static async Task<bool> Run(ToolArgumentCaptureFilter filter, ToolInvocationContext ctx)
    {
        var nextRan = false;
        await filter.OnToolInvocationAsync(ctx, c => { nextRan = true; c.Result = "fn-result"; return Task.CompletedTask; });
        return nextRan;
    }

    // ── Argument capture ──────────────────────────────────────────────────────

    [Fact]
    public async Task DescriptorPresent_ArgumentsPopulated_ProposesEachValueAndSwearsToNone()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor("CaptureFn", "TestPlugin", Operation.WriteCreate, "TestEntity", null));

        await Run(BuildFilter(fabric, registry), Ctx(new Dictionary<string, object?>
        {
            ["title"] = "my title",
            ["priority"] = "High"
        }));

        var entity = fabric.GetByKey("TestEntity");

        Assert.NotNull(entity);
        Assert.Equal("my title", entity.Fields["title"]);
        Assert.Equal("High", entity.Fields["priority"]);

        // PV-1: what the model wrote into the call is the value it proposes and says nothing about
        // where that value came from. The capture mints no tag; a deterministic interceptor or the
        // host's inference port is what swears for a field, and where neither speaks the projection
        // swears the field Empty at confidence 0.
        Assert.Null(fabric.GetFieldChain("title"));
        Assert.Null(fabric.GetFieldChain("priority"));
    }

    [Fact]
    public async Task DescriptorPresent_EmptyArguments_NoFabricWrite()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor("CaptureFn", "TestPlugin", Operation.WriteCreate, "TestEntity", null));

        await Run(BuildFilter(fabric, registry), Ctx(new Dictionary<string, object?>()));

        Assert.Null(fabric.GetFieldChain("title"));
    }

    [Fact]
    public async Task NoDescriptor_ArgumentsIgnored_NoFabricWrite()
    {
        var fabric = new ContextFabric();
        await Run(BuildFilter(fabric, new AffiantToolRegistry()),
            Ctx(new Dictionary<string, object?> { ["title"] = "ignored" }));

        Assert.Null(fabric.GetFieldChain("title"));
    }

    // ── Next always fires ─────────────────────────────────────────────────────

    [Fact]
    public async Task NextAlwaysFires_WithDescriptorAndArguments()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor("CaptureFn", "TestPlugin", Operation.WriteCreate, "TestEntity", null));

        var ran = await Run(BuildFilter(fabric, registry), Ctx(new Dictionary<string, object?> { ["x"] = "v" }));
        Assert.True(ran);
    }

    [Fact]
    public async Task NextAlwaysFires_WithoutDescriptor()
    {
        var fabric = new ContextFabric();
        var ran = await Run(BuildFilter(fabric, new AffiantToolRegistry()), Ctx(new Dictionary<string, object?>()));
        Assert.True(ran);
    }
}
