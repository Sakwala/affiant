namespace Affiant.Core.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class AddAffidavitProjectionTests
{
    // --- Fakes ---

    private sealed class WidgetProjection : IAffidavitProjection
    {
        public string EntityType => "Widget";
        public Affidavit Project(IContextFabric fabric, string operationType, IReadOnlyList<string> warnings)
            => throw new NotImplementedException();
    }

    private sealed class GadgetProjection : IAffidavitProjection
    {
        public string EntityType => "Gadget";
        public Affidavit Project(IContextFabric fabric, string operationType, IReadOnlyList<string> warnings)
            => throw new NotImplementedException();
    }

    // --- Test 1: resolves same instance as concrete type ---

    [Fact]
    public void AddAffidavitProjection_SameInstanceAsConcreteType()
    {
        var services = new ServiceCollection();
        services.AddAffidavitProjection<WidgetProjection>();
        var provider = services.BuildServiceProvider();

        var viaInterface = provider.GetRequiredService<IAffidavitProjection>();
        var viaConcrete = provider.GetRequiredService<WidgetProjection>();

        Assert.Same(viaConcrete, viaInterface);
    }

    // --- Test 2: two projections for two entity types both resolve ---

    [Fact]
    public void TwoProjections_BothResolveViaGetServices()
    {
        var services = new ServiceCollection();
        services.AddAffidavitProjection<WidgetProjection>();
        services.AddAffidavitProjection<GadgetProjection>();
        var provider = services.BuildServiceProvider();

        var all = provider.GetServices<IAffidavitProjection>().ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p is WidgetProjection);
        Assert.Contains(all, p => p is GadgetProjection);
    }

    // --- Test 3: idempotent registration of same TProjection ---

    [Fact]
    public void IdempotentRegistration_DoesNotDoubleRegister()
    {
        var services = new ServiceCollection();
        services.AddAffidavitProjection<WidgetProjection>();
        services.AddAffidavitProjection<WidgetProjection>(); // second call is no-op

        var provider = services.BuildServiceProvider();
        var all = provider.GetServices<IAffidavitProjection>().ToList();

        Assert.Single(all);
    }

    // --- Test 4: returns IServiceCollection for chaining ---

    [Fact]
    public void Returns_IServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddAffidavitProjection<WidgetProjection>();
        Assert.Same(services, returned);
    }
}
