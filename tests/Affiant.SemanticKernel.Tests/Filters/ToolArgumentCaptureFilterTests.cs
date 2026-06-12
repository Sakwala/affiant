namespace Affiant.SemanticKernel.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

/// <summary>
/// Unit tests for ToolArgumentCaptureFilter.
/// Verifies that LLM-populated tool arguments are captured into IContextFabric via
/// ProvenanceTag.FromTool (Source = Conversation), that unregistered tools are skipped,
/// and that next() always fires.
/// </summary>
public class ToolArgumentCaptureFilterTests
{
    // ── Argument capture ──────────────────────────────────────────────────────

    [Fact]
    public async Task DescriptorPresent_ArgumentsPopulated_SetsFieldChainPerArgument()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor(
            "CaptureFn", "TestPlugin",
            Operation.WriteCreate, "TestEntity", null));

        var kernel = BuildKernel(BuildFilter(fabric, registry));

        await kernel.InvokeAsync("TestPlugin", "CaptureFn", new KernelArguments
        {
            ["title"] = "my title",
            ["priority"] = "High"
        });

        var titleChain = fabric.GetFieldChain("title");
        var priorityChain = fabric.GetFieldChain("priority");

        Assert.NotNull(titleChain);
        Assert.NotNull(priorityChain);

        // ProvenanceTag.FromTool returns Source = Conversation (existing factory; see PRD §3.3 deviation note)
        Assert.Equal(ProvenanceSource.Conversation, titleChain.Current.Source);
        Assert.Equal(ProvenanceSource.Conversation, priorityChain.Current.Source);
        Assert.InRange(titleChain.Current.Confidence, 0.89f, 0.91f);
    }

    [Fact]
    public async Task DescriptorPresent_EmptyArguments_NoFabricWrite()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor(
            "CaptureFn", "TestPlugin",
            Operation.WriteCreate, "TestEntity", null));

        var kernel = BuildKernel(BuildFilter(fabric, registry));

        await kernel.InvokeAsync("TestPlugin", "CaptureFn");

        // No arguments → nothing written to fabric
        Assert.Null(fabric.GetFieldChain("title"));
    }

    [Fact]
    public async Task NoDescriptor_ArgumentsIgnored_NoFabricWrite()
    {
        var fabric = new ContextFabric();
        // Empty registry — CaptureFn not registered
        var kernel = BuildKernel(BuildFilter(fabric, new AffiantToolRegistry()));

        await kernel.InvokeAsync("TestPlugin", "CaptureFn", new KernelArguments
        {
            ["title"] = "ignored"
        });

        Assert.Null(fabric.GetFieldChain("title"));
    }

    // ── Next always fires ─────────────────────────────────────────────────────

    [Fact]
    public async Task NextAlwaysFires_WithDescriptorAndArguments()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor(
            "CaptureFn", "TestPlugin",
            Operation.WriteCreate, "TestEntity", null));

        var kernel = BuildKernel(BuildFilter(fabric, registry));

        var result = await kernel.InvokeAsync("TestPlugin", "CaptureFn",
            new KernelArguments { ["x"] = "v" });

        Assert.Equal("fn-result", result.GetValue<string>());
    }

    [Fact]
    public async Task NextAlwaysFires_WithoutDescriptor()
    {
        var fabric = new ContextFabric();
        var kernel = BuildKernel(BuildFilter(fabric, new AffiantToolRegistry()));

        var result = await kernel.InvokeAsync("TestPlugin", "CaptureFn");

        Assert.Equal("fn-result", result.GetValue<string>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ToolArgumentCaptureFilter BuildFilter(
        IContextFabric fabric, IAffiantToolRegistry registry)
        => new(fabric, registry, NullLogger<ToolArgumentCaptureFilter>.Instance);

    private static Kernel BuildKernel(ToolArgumentCaptureFilter filter)
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.FunctionInvocationFilters.Add(filter);
        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("TestPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "fn-result", "CaptureFn")]));
        return kernel;
    }
}
