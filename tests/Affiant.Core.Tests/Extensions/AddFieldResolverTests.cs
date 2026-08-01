namespace Affiant.Core.Tests.Extensions;

using System.Threading;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class AddFieldResolverTests
{
    // --- Fakes ---

    private sealed class ColorResolver : IFieldResolver
    {
        public string FieldName => "Color";
        public Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult<FieldResolution?>(new FieldResolution("Red", ProvenanceTag.FromUser("Color")));
    }

    private sealed class WeightResolver : IFieldResolver
    {
        public string FieldName => "Weight";
        public Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult<FieldResolution?>(new FieldResolution("1.5", ProvenanceTag.FromUser("Weight")));
    }

    // --- Test 1: resolves same instance as concrete type ---

    [Fact]
    public void AddFieldResolver_SameInstanceAsConcreteType()
    {
        var services = new ServiceCollection();
        services.AddFieldResolver<ColorResolver>();
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var viaInterface = scope.ServiceProvider.GetRequiredService<IFieldResolver>();
        var viaConcrete = scope.ServiceProvider.GetRequiredService<ColorResolver>();

        Assert.Same(viaConcrete, viaInterface);
    }

    // --- Test 2: multiple resolvers for different fields both resolve ---

    [Fact]
    public void MultipleResolvers_BothResolveViaGetServices()
    {
        var services = new ServiceCollection();
        services.AddFieldResolver<ColorResolver>();
        services.AddFieldResolver<WeightResolver>();
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var all = scope.ServiceProvider.GetServices<IFieldResolver>().ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r is ColorResolver);
        Assert.Contains(all, r => r is WeightResolver);
    }

    // --- Test 3: idempotent registration of same TResolver ---

    [Fact]
    public void IdempotentRegistration_DoesNotDoubleRegister()
    {
        var services = new ServiceCollection();
        services.AddFieldResolver<ColorResolver>();
        services.AddFieldResolver<ColorResolver>(); // second call is no-op

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var all = scope.ServiceProvider.GetServices<IFieldResolver>().ToList();

        Assert.Single(all);
    }

    // --- Test 4: returns IServiceCollection for chaining ---

    [Fact]
    public void Returns_IServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddFieldResolver<ColorResolver>();
        Assert.Same(services, returned);
    }

    // --- Test 5: registered Scoped, not Singleton (unlike AddDeterministicFieldSource) ---

    [Fact]
    public void AddFieldResolver_RegistersScoped_NotSingleton()
    {
        var services = new ServiceCollection();
        services.AddFieldResolver<ColorResolver>();

        var descriptor = services.Single(d => d.ServiceType == typeof(ColorResolver));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    // --- Test 6: async resolver with a DI-scoped dependency works end-to-end ---

    private interface IScopedLookup
    {
        string Lookup();
    }

    private sealed class ScopedLookup : IScopedLookup
    {
        private static int _instanceCounter;
        private readonly int _id = Interlocked.Increment(ref _instanceCounter);
        public string Lookup() => $"scoped-value-{_id}";
    }

    private sealed class ScopedDependencyStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Color", "string", "Color of the widget"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class ScopedDependencyResolver : IFieldResolver
    {
        private readonly IScopedLookup _lookup;
        public ScopedDependencyResolver(IScopedLookup lookup) => _lookup = lookup;
        public string FieldName => "Color";

        public async Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken)
        {
            // Genuinely yields — proves the sync-over-async bridge in SchemaDrivenAffidavitProjection
            // correctly awaits real asynchronous work, not just a synchronously-completed Task.
            await Task.Yield();
            return new FieldResolution(_lookup.Lookup(), ProvenanceTag.FromUser("Color"));
        }
    }

    [Fact]
    public void AsyncResolver_WithDiScopedDependency_WorksEndToEnd()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddScoped<IScopedLookup, ScopedLookup>();
        services.AddFieldResolver<ScopedDependencyResolver>();
        services.AddSingleton<ITaskInferenceStrategy, ScopedDependencyStrategy>();
        services.AddScoped<IAffidavitProjection, SchemaDrivenAffidavitProjection>();

        // ValidateScopes: true makes this test a genuine proof there is no captive-dependency
        // violation (Scoped IFieldResolver consumed from a Scoped IAffidavitProjection) — with
        // the default BuildServiceProvider() this would silently pass even if lifetimes were wrong.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var projection = scope.ServiceProvider.GetRequiredService<IAffidavitProjection>();
        var fabric = scope.ServiceProvider.GetRequiredService<IContextFabric>();

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.StartsWith("scoped-value-", (string)colorField.Value!);
        Assert.Equal(ProvenanceSource.UserStated, colorField.Provenance.Current.Source);
    }
}
