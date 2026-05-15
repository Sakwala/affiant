namespace Affiant.Core.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class AddDeterministicFieldSourceTests
{
    // --- Fakes ---

    private sealed class ColorSource : IDeterministicFieldSource
    {
        public string FieldName => "Color";
        public ProvenanceTag? Resolve(IContextFabric fabric) => ProvenanceTag.FromUser("Color");
    }

    private sealed class WeightSource : IDeterministicFieldSource
    {
        public string FieldName => "Weight";
        public ProvenanceTag? Resolve(IContextFabric fabric) => ProvenanceTag.FromUser("Weight");
    }

    // --- Test 1: resolves same instance as concrete type ---

    [Fact]
    public void AddDeterministicFieldSource_SameInstanceAsConcreteType()
    {
        var services = new ServiceCollection();
        services.AddDeterministicFieldSource<ColorSource>();
        var provider = services.BuildServiceProvider();

        var viaInterface = provider.GetRequiredService<IDeterministicFieldSource>();
        var viaConcrete = provider.GetRequiredService<ColorSource>();

        Assert.Same(viaConcrete, viaInterface);
    }

    // --- Test 2: multiple sources for different fields both resolve ---

    [Fact]
    public void MultipleSources_BothResolveViaGetServices()
    {
        var services = new ServiceCollection();
        services.AddDeterministicFieldSource<ColorSource>();
        services.AddDeterministicFieldSource<WeightSource>();
        var provider = services.BuildServiceProvider();

        var all = provider.GetServices<IDeterministicFieldSource>().ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s is ColorSource);
        Assert.Contains(all, s => s is WeightSource);
    }

    // --- Test 3: idempotent registration of same TSource ---

    [Fact]
    public void IdempotentRegistration_DoesNotDoubleRegister()
    {
        var services = new ServiceCollection();
        services.AddDeterministicFieldSource<ColorSource>();
        services.AddDeterministicFieldSource<ColorSource>(); // second call is no-op

        var provider = services.BuildServiceProvider();
        var all = provider.GetServices<IDeterministicFieldSource>().ToList();

        Assert.Single(all);
    }

    // --- Test 4: returns IServiceCollection for chaining ---

    [Fact]
    public void Returns_IServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddDeterministicFieldSource<ColorSource>();
        Assert.Same(services, returned);
    }
}
