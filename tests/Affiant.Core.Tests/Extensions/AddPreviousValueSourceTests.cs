namespace Affiant.Core.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Registration of the host port the built-in projection asks for the values an update replaces.
/// </summary>
public class AddPreviousValueSourceTests
{
    private sealed class WidgetPreviousValues : IPreviousValueSource
    {
        public Task<IReadOnlyDictionary<string, object?>?> GetPreviousValuesAsync(
            string entityType, string entityId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?>());
    }

    private sealed class GadgetPreviousValues : IPreviousValueSource
    {
        public Task<IReadOnlyDictionary<string, object?>?> GetPreviousValuesAsync(
            string entityType, string entityId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, object?>?>(null);
    }

    [Fact]
    public void ResolvesAsTheInterfaceAndAsTheConcreteType_SameInstance()
    {
        var services = new ServiceCollection();
        services.AddPreviousValueSource<WidgetPreviousValues>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<WidgetPreviousValues>(),
            Assert.Single(scope.ServiceProvider.GetServices<IPreviousValueSource>()));
    }

    [Fact]
    public void MultipleSources_AllResolveFromTheEnumerable()
    {
        var services = new ServiceCollection();
        services.AddPreviousValueSource<WidgetPreviousValues>();
        services.AddPreviousValueSource<GadgetPreviousValues>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal(2, scope.ServiceProvider.GetServices<IPreviousValueSource>().Count());
    }

    [Fact]
    public void CallingTwiceWithTheSameType_IsANoOp()
    {
        var services = new ServiceCollection();
        services.AddPreviousValueSource<WidgetPreviousValues>();
        services.AddPreviousValueSource<WidgetPreviousValues>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Single(scope.ServiceProvider.GetServices<IPreviousValueSource>());
    }

    [Fact]
    public void IsRegisteredScoped_SoASourceMayTakeAScopedDependency()
    {
        var services = new ServiceCollection();
        services.AddPreviousValueSource<WidgetPreviousValues>();

        Assert.All(
            services.Where(d => d.ServiceType == typeof(IPreviousValueSource)
                             || d.ServiceType == typeof(WidgetPreviousValues)),
            d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }
}
