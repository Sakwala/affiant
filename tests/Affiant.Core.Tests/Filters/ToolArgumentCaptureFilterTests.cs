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
/// Verifies that LLM-populated tool arguments are captured into IContextFabric via
/// ProvenanceTag.FromTool (Source = Conversation), that unregistered tools are skipped,
/// and that next() always fires.
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
    public async Task DescriptorPresent_ArgumentsPopulated_SetsFieldChainPerArgument()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor("CaptureFn", "TestPlugin", Operation.WriteCreate, "TestEntity", null));

        await Run(BuildFilter(fabric, registry), Ctx(new Dictionary<string, object?>
        {
            ["title"] = "my title",
            ["priority"] = "High"
        }));

        var titleChain = fabric.GetFieldChain("title");
        var priorityChain = fabric.GetFieldChain("priority");

        Assert.NotNull(titleChain);
        Assert.NotNull(priorityChain);
        Assert.Equal(ProvenanceSource.Conversation, titleChain.Current.Source);
        Assert.Equal(ProvenanceSource.Conversation, priorityChain.Current.Source);
        Assert.InRange(titleChain.Current.Confidence, 0.89f, 0.91f);
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
