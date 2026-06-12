namespace Affiant.SemanticKernel.Tests.Extensions;

using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.SemanticKernel.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

public class AddAffiantPluginsFromTypeTests
{
    // ── Test fixtures ──────────────────────────────────────────────────────────

    private sealed class TestPluginReadOnly
    {
        [KernelFunction]
        public string ReadSomething() => "result";

        // Not decorated with [KernelFunction] — must be skipped by the walker.
        public string NotAPlugin() => "not registered";
    }

    private sealed class TestPluginWithWrite
    {
        [KernelFunction]
        [AffiantWriteTool("WriteCreate", "TestEntity", typeof(FakeStrategy))]
        public string CreateSomething() => "created";

        [KernelFunction]
        public string GetContext() => "context";
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IServiceProvider BuildServiceProvider<T>(string? pluginName = null) where T : class
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();

        var builder = Kernel.CreateBuilder();
        foreach (var sd in services)
            builder.Services.Add(sd);

        builder.AddAffiantPluginsFromType<T>(pluginName);

        return builder.Services.BuildServiceProvider();
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WalksTargetType_RegistersKernelFunctionMethods()
    {
        var sp = BuildServiceProvider<TestPluginReadOnly>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();
        var all = registry.All.ToList();

        // ReadSomething has [KernelFunction]; NotAPlugin does not — only ReadSomething registered.
        Assert.Single(all);
        Assert.Equal("ReadSomething", all[0].FunctionName);
        Assert.Equal("TestPluginReadOnly", all[0].PluginName);
    }

    [Fact]
    public void PluginName_DefaultsToTypeName()
    {
        var sp = BuildServiceProvider<TestPluginReadOnly>(pluginName: null);
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.Equal("TestPluginReadOnly", registry.All.Single().PluginName);
    }

    [Fact]
    public void PluginName_HonorsExplicitValue()
    {
        var sp = BuildServiceProvider<TestPluginReadOnly>(pluginName: "CustomName");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.Equal("CustomName", registry.All.Single().PluginName);
    }

    [Fact]
    public void WriteAttribute_DetectedAndPreserved()
    {
        var sp = BuildServiceProvider<TestPluginWithWrite>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("CreateSomething", "TestPluginWithWrite");

        Assert.NotNull(descriptor);
        Assert.Equal("WriteCreate", descriptor.Operation.Kind);
        Assert.Equal("TestEntity", descriptor.EntityType);
        Assert.Equal(typeof(FakeStrategy), descriptor.InferenceStrategy);
    }

    [Fact]
    public void ReadByAbsence_HasNullEntityAndStrategy()
    {
        var sp = BuildServiceProvider<TestPluginWithWrite>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("GetContext", "TestPluginWithWrite");

        Assert.NotNull(descriptor);
        Assert.Equal(Operation.ReadQuery, descriptor.Operation);
        Assert.Null(descriptor.EntityType);
        Assert.Null(descriptor.InferenceStrategy);
    }

    [Fact]
    public void NonKernelFunctionMethods_AreSkipped()
    {
        var sp = BuildServiceProvider<TestPluginWithWrite>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();
        var all = registry.All.ToList();

        // TestPluginWithWrite has exactly 2 [KernelFunction] methods.
        Assert.Equal(2, all.Count);
        Assert.All(all, d => Assert.Equal("TestPluginWithWrite", d.PluginName));
    }

    [Fact]
    public void ThrowsOnMissingRegistry()
    {
        // Skip AddAffiantCore() so IAffiantToolRegistry is absent.
        var builder = Kernel.CreateBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddAffiantPluginsFromType<TestPluginReadOnly>());

        Assert.Contains("AddAffiantCore", ex.Message);
    }

    [Fact]
    public void EmptyPluginType_RegistersNothing()
    {
        // string has no [KernelFunction] methods — walker must return cleanly with zero registrations.
        var sp = BuildServiceProvider<string>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.Empty(registry.All);
    }
}
