namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class AffiantToolRegistryTests
{
    private static IAffiantToolRegistry CreateRegistry() => new AffiantToolRegistry();

    private static AffiantToolDescriptor Descriptor(string functionName, string? pluginName, string? entityType = null) =>
        new(functionName, pluginName, Operation.ReadQuery, entityType, null);

    [Fact]
    public void Register_ThenFind_ReturnsSameDescriptor()
    {
        var registry = CreateRegistry();
        var descriptor = Descriptor("GetFleet", "FleetPlugin");

        registry.Register(descriptor);

        var found = registry.Find("GetFleet", "FleetPlugin");
        Assert.Same(descriptor, found);
        Assert.Single(registry.All);
    }

    [Fact]
    public void Register_Twice_ThrowsAndMessageNamesBoth()
    {
        var registry = CreateRegistry();
        var first = Descriptor("CreateThing", "P1", "EntityA");
        var second = Descriptor("CreateThing", "P1", "EntityB");

        registry.Register(first);
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(second));

        Assert.Contains("EntityA", ex.Message);
        Assert.Contains("EntityB", ex.Message);
    }

    [Fact]
    public void Find_WithoutPluginName_ReturnsMatch_WhenOnlyOnePlugin()
    {
        var registry = CreateRegistry();
        var descriptor = Descriptor("CreateThing", "P1");

        registry.Register(descriptor);

        var found = registry.Find("CreateThing", null);
        Assert.Same(descriptor, found);
    }

    [Fact]
    public void Find_WithoutPluginName_Throws_WhenMultiplePluginsShareFunction()
    {
        var registry = CreateRegistry();
        registry.Register(Descriptor("CreateThing", "P1"));
        registry.Register(Descriptor("CreateThing", "P2"));

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Find("CreateThing", null));

        Assert.Contains("P1", ex.Message);
        Assert.Contains("P2", ex.Message);
    }

    [Fact]
    public void Find_NotPresent_ReturnsNull()
    {
        var registry = CreateRegistry();

        Assert.Null(registry.Find("Anything", "P1"));
        Assert.Null(registry.Find("Anything", null));
    }

    [Fact]
    public void All_ReturnsSnapshot_NotLiveView()
    {
        var registry = CreateRegistry();
        registry.Register(Descriptor("F1", "P1"));
        registry.Register(Descriptor("F2", "P1"));

        var snapshot = registry.All;
        Assert.Equal(2, snapshot.Count);

        registry.Register(Descriptor("F3", "P1"));

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(3, registry.All.Count);
    }

    [Fact]
    public void Register_Concurrent_AllDescriptorsLand()
    {
        var registry = CreateRegistry();

        Parallel.For(0, 200, i =>
            registry.Register(new AffiantToolDescriptor($"F{i}", null, Operation.ReadQuery, null, null)));

        Assert.Equal(200, registry.All.Count);
        Assert.NotNull(registry.Find("F123", null));
    }

    [Fact]
    public void AddAffiantCore_RegistersDefaultRegistryAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IAffiantToolRegistry>();
        Assert.IsType<AffiantToolRegistry>(registry);

        var registry2 = provider.GetRequiredService<IAffiantToolRegistry>();
        Assert.Same(registry, registry2);
    }

    [Fact]
    public void HostCanReplaceImplementation_BeforeAddAffiantCore()
    {
        var fake = new FakeRegistry();
        var services = new ServiceCollection();
        services.AddSingleton<IAffiantToolRegistry>(fake);
        services.AddAffiantCore();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IAffiantToolRegistry>();
        Assert.Same(fake, registry);
    }

    private sealed class FakeRegistry : IAffiantToolRegistry
    {
        public void Register(AffiantToolDescriptor descriptor) { }
        public AffiantToolDescriptor? Find(string functionName, string? pluginName = null) => null;
        public IReadOnlyList<AffiantToolDescriptor> All => [];
    }
}
