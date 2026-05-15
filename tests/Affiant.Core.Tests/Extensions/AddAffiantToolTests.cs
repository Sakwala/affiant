namespace Affiant.Core.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class AddAffiantToolTests
{
    private static IServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        configure(services);
        return services.BuildServiceProvider();
    }

    // --- Test 1 ---
    [Fact]
    public void AddAffiantTool_RegistersBothStrategyAndDescriptor_Atomically()
    {
        var provider = Build(s =>
            s.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing", "P"));

        var strategy = provider.GetRequiredService<FakeStrategy>();
        Assert.NotNull(strategy);

        var registry = provider.GetRequiredService<IAffiantToolRegistry>();
        var descriptor = registry.Find("CreateThing", "P");
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(FakeStrategy), descriptor.InferenceStrategy);
        Assert.Equal(Operation.WriteCreate, descriptor.Operation);
        Assert.Equal("Thing", descriptor.EntityType);
        Assert.Equal("P", descriptor.PluginName);
    }

    // --- Test 2 ---
    [Fact]
    public void AddAffiantReadTool_RegistersReadDescriptor_NoStrategy()
    {
        var provider = Build(s =>
            s.AddAffiantReadTool("FindThings", entityType: "Thing", pluginName: "P"));

        var registry = provider.GetRequiredService<IAffiantToolRegistry>();
        var descriptor = registry.Find("FindThings", "P");
        Assert.NotNull(descriptor);
        Assert.Equal(Operation.ReadQuery, descriptor.Operation);
        Assert.Null(descriptor.InferenceStrategy);
        Assert.Equal("Thing", descriptor.EntityType);

        // No strategy registered as a side effect.
        Assert.Null(provider.GetService<FakeStrategy>());
    }

    // --- Test 3 ---
    [Fact]
    public void AddAffiantTool_DoubleRegistration_Throws()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing", "P");

        Assert.Throws<InvalidOperationException>(() =>
            services.AddAffiantTool<OtherFakeStrategy>("CreateThing", Operation.WriteCreate, "Thing", "P"));
    }

    // --- Test 4 ---
    // Cross-path idempotency: a descriptor registered via AddAffiantReadTool cannot be re-registered
    // via AddAffiantTool (and vice versa). Both paths converge on IAffiantToolRegistry.Register(),
    // which enforces the idempotency contract from Story 15.2. This is the same invariant that
    // guards against mixing AddAffiantPluginsFromAssembly (15.3) with AddAffiantTool (15.4)
    // for the same function — both paths call registry.Register() and the registry rejects duplicates.
    [Fact]
    public void MixedPaths_AddAffiantReadTool_ThenAddAffiantTool_SameFunction_Throws()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantReadTool("CreateThing", "Thing", "P");

        Assert.Throws<InvalidOperationException>(() =>
            services.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing", "P"));
    }

    // --- Test 5 ---
    [Fact]
    public void AddAffiantTool_ResolvableThroughDI()
    {
        var provider = Build(s =>
            s.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing"));

        // Startup validator Check B (15.5) resolves by concrete type, not interface.
        Assert.NotNull(provider.GetService(typeof(FakeStrategy)));
        Assert.NotNull(provider.GetRequiredService<FakeStrategy>());

        // Does not register by interface (by design — see Gotcha 4 in story 15.4).
        Assert.Null(provider.GetService<ITaskInferenceStrategy>());
    }

    // --- Test 6 ---
    [Fact]
    public void AddAffiantReadTool_DoesNotRegisterStrategy()
    {
        var provider = Build(s =>
            s.AddAffiantReadTool("FindThings"));

        Assert.Null(provider.GetService<FakeStrategy>());
        Assert.Null(provider.GetService<OtherFakeStrategy>());
        Assert.Null(provider.GetService<ITaskInferenceStrategy>());
    }

    // --- Test 7 ---
    [Fact]
    public void AddAffiantTool_Throws_WhenAddAffiantCoreNotCalledFirst()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddAffiantTool<FakeStrategy>("CreateThing", Operation.WriteCreate, "Thing"));

        Assert.Contains("AddAffiantCore", ex.Message);
    }

    // --- Test 8 ---
    [Fact]
    public void AddAffiantReadTool_Throws_WhenAddAffiantCoreNotCalledFirst()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddAffiantReadTool("FindThings"));

        Assert.Contains("AddAffiantCore", ex.Message);
    }

    // --- Test 9 ---
    [Fact]
    public void Returns_IServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();

        var returned1 = services.AddAffiantTool<FakeStrategy>(
            "CreateThing", Operation.WriteCreate, "Thing", "P");
        Assert.Same(services, returned1);

        var returned2 = services.AddAffiantReadTool("FindThings", "Thing", "P2");
        Assert.Same(services, returned2);
    }

    // --- Test 10 (Gotcha 5) ---
    [Fact]
    public void AddAffiantTool_WithReadQueryOperation_Throws()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddAffiantTool<FakeStrategy>("FindThings", Operation.ReadQuery, "Thing"));

        Assert.Contains("ReadQuery", ex.Message);
        Assert.Contains("AddAffiantReadTool", ex.Message);
    }

    // --- Fakes ---

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class OtherFakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "OtherThing";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }
}
