namespace Affiant.Extensions.AI.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Affiant.Extensions.AI.Extensions;
using Affiant.Extensions.AI.Filters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Wire-up contract for <see cref="ChatOptionsExtensions.WithAffiant"/>: the double-wrap guard
/// (design decision 6 of the M.E.AI adapter brief,
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>), the
/// missing-registration refusals, and the tool-list composition rules the guard depends on.
/// </summary>
public class WithAffiantWiringTests
{
    // ── Decision 6: the double-wrap guard ────────────────────────────────────

    /// <summary>
    /// The common mistake the marker exists to catch: <c>WithAffiant</c> called a second time on the
    /// options it already returned. Running the neutral onion twice for one logical tool call
    /// double-tags provenance, fires inference twice, and files the same write proposal onto the
    /// docket twice — a silent semantic corruption, so it must fail loudly at wire-up instead.
    /// </summary>
    [Fact]
    public void RewiringAlreadyWiredChatOptions_Throws()
    {
        var sp = BuildServices().BuildServiceProvider();
        var catalog = AffiantToolCatalog.FromType<SampleTools>();

        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        var ex = Assert.Throws<InvalidOperationException>(
            () => wired.WithAffiant(sp, AffiantToolCatalog.FromType<OtherTools>()));

        Assert.Contains("DoThing", ex.Message, StringComparison.Ordinal);
        // The message must also carry the half the guard cannot enforce, since a host hitting this
        // error is exactly the host at risk of the cross-adapter version of the same mistake.
        Assert.Contains("Affiant.AgentFramework", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second half of the mistake: a catalog whose functions were wrapped at another wiring site
    /// and then reused. The guard looks at the catalog's own functions, not only the options', so
    /// sharing a wired catalog is refused too.
    /// </summary>
    [Fact]
    public void WiringACatalogWhoseFunctionsAreAlreadyWrapped_Throws()
    {
        var sp = BuildServices().BuildServiceProvider();
        var catalog = AffiantToolCatalog.FromType<SampleTools>();

        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        var reusedCatalog = new AffiantToolCatalog(
            [.. wired.Tools!.OfType<AIFunction>()], catalog.Descriptors);

        Assert.Throws<InvalidOperationException>(
            () => new ChatOptions().WithAffiant(sp, reusedCatalog));
    }

    /// <summary>
    /// The guard runs before the registry is touched, so a refused re-wiring is a pure no-op — same
    /// ordering rule the hosted-tool audit follows, and for the same reason.
    /// </summary>
    [Fact]
    public void RefusedDoubleWrap_LeavesRegistryUnchanged()
    {
        var sp = BuildServices().BuildServiceProvider();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var catalog = AffiantToolCatalog.FromType<SampleTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);
        var countAfterFirstWiring = registry.All.Count;

        Assert.Throws<InvalidOperationException>(
            () => wired.WithAffiant(sp, AffiantToolCatalog.FromType<OtherTools>()));

        Assert.Equal(countAfterFirstWiring, registry.All.Count);
    }

    [Fact]
    public void WrappedFunction_ExposesItsInnerFunction_ThroughTheMarker()
    {
        var sp = BuildServices().BuildServiceProvider();
        var catalog = AffiantToolCatalog.FromType<SampleTools>();
        var original = catalog.Functions.Single();

        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        var marker = Assert.IsAssignableFrom<IAffiantWrappedFunction>(Assert.Single(wired.Tools!));
        Assert.Same(original, marker.AffiantInnerFunction);
    }

    // ── Tool-list composition ────────────────────────────────────────────────

    [Fact]
    public void WiringReturnsAClone_AndLeavesTheCallersOptionsUnwrapped()
    {
        var sp = BuildServices().BuildServiceProvider();
        var catalog = AffiantToolCatalog.FromType<SampleTools>();
        var original = new ChatOptions { Tools = [.. catalog.Functions] };

        var wired = original.WithAffiant(sp, catalog);

        Assert.NotSame(original, wired);
        Assert.DoesNotContain(original.Tools!, t => t is IAffiantWrappedFunction);
        Assert.All(wired.Tools!, t => Assert.IsAssignableFrom<IAffiantWrappedFunction>(t));
    }

    /// <summary>
    /// A function already on the options and also in the catalog must be wrapped once, not appended
    /// twice — the de-duplication is by LLM-visible name, which is the identity the model sees.
    /// </summary>
    [Fact]
    public void CatalogFunctionAlreadyOnTheOptions_IsNotAppendedTwice()
    {
        var sp = BuildServices().BuildServiceProvider();
        var catalog = AffiantToolCatalog.FromType<SampleTools>();

        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        Assert.Single(wired.Tools!);
    }

    /// <summary>
    /// An <see cref="AIFunction"/> the host put on the options but never declared in the catalog is
    /// still wrapped — matching the MAF adapter, which likewise rewrites every function on the agent.
    /// Coverage follows the tool list, not the catalog.
    /// </summary>
    [Fact]
    public void FunctionOnTheOptionsButNotInTheCatalog_IsStillWrapped()
    {
        var sp = BuildServices().BuildServiceProvider();
        var stray = AIFunctionFactory.Create((Func<string>)(() => "ok"), name: "Stray");

        var wired = new ChatOptions { Tools = [stray] }
            .WithAffiant(sp, AffiantToolCatalog.FromType<SampleTools>());

        var wrappedStray = wired.Tools!.OfType<AIFunction>().Single(f => f.Name == "Stray");
        Assert.IsAssignableFrom<IAffiantWrappedFunction>(wrappedStray);
        Assert.Equal(2, wired.Tools!.Count);
    }

    // ── Missing wire-up ──────────────────────────────────────────────────────

    [Fact]
    public void WithoutAddAffiantCore_ThrowsNamingTheMissingCall()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ChatOptions().WithAffiant(sp, AffiantToolCatalog.FromType<SampleTools>()));

        Assert.Contains("AddAffiantCore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAddAffiantExtensionsAI_ThrowsNamingTheMissingCall()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ChatOptions().WithAffiant(sp, AffiantToolCatalog.FromType<SampleTools>()));

        Assert.Contains("AddAffiantExtensionsAI", ex.Message, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantExtensionsAI();
        return services;
    }

    private sealed class SampleTools
    {
        public string DoThing(string value) => value;
    }

    private sealed class OtherTools
    {
        public string DoOtherThing(string value) => value;
    }
}
