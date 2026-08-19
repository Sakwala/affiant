namespace Affiant.Extensions.AI.Tests.Extensions;

using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

using ExtensionsAIToolName = Affiant.Extensions.AI.Attributes.AffiantToolNameAttribute;
using MafCatalog = Affiant.AgentFramework.AffiantToolCatalog;
using MafToolName = Affiant.AgentFramework.Attributes.AffiantToolNameAttribute;

/// <summary>
/// Design decision 4 of the M.E.AI adapter brief
/// (<c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>): tool identity
/// stays <c>[AffiantToolName]</c>, and this package's name-resolution precedence follows the
/// Microsoft Agent Framework adapter's exactly. This is the test that pins it.
///
/// <para>
/// Why it is worth pinning rather than trusting: decision 3 made this package's
/// <c>AffiantToolCatalog</c> a <em>copy</em> of the MAF adapter's rather than a reference to it, to
/// avoid an adapter-to-adapter dependency before the post-beta consolidation. A copy drifts. These
/// tests turn drift into a build failure — for the whole descriptor set, for the override mechanism,
/// and for the two name-derivation rules most likely to be "simplified" by a later edit.
/// </para>
///
/// <para>
/// The MAF adapter is referenced by this <em>test project only</em>; the shipped
/// <c>Affiant.Extensions.AI</c> assembly has no such reference, which
/// <c>Affiant.Extensions.AI.Tests.Layering.PackageLayeringTests</c> asserts.
/// </para>
/// </summary>
public class CatalogReflectionParityTests
{
    [Fact]
    public void FromType_ProducesTheSameDescriptorSet_AsTheMafCatalog()
    {
        var mine = AffiantToolCatalog.FromType<ParityTools>("Widgets").Descriptors;
        var maf = MafCatalog.FromType<MafParityTools>("Widgets").Descriptors;

        Assert.Equal(maf.Count, mine.Count);

        foreach (var expected in maf)
        {
            var actual = Assert.Single(mine, d => d.FunctionName == expected.FunctionName);
            Assert.Equal(expected.PluginName, actual.PluginName);
            Assert.Equal(expected.Operation, actual.Operation);
            Assert.Equal(expected.EntityType, actual.EntityType);
            // The strategy types are per-fixture, so compare the shape both catalogs must carry:
            // a write tool names a strategy, a read tool names none.
            Assert.Equal(expected.InferenceStrategy is null, actual.InferenceStrategy is null);
        }
    }

    [Fact]
    public void AffiantToolNameOverride_ProducesTheSameLlmVisibleName_AsTheMafCatalog()
    {
        var mine = AffiantToolCatalog.FromType<OverriddenTools>();
        var maf = MafCatalog.FromType<MafOverriddenTools>();

        Assert.Equal("search_widget", Assert.Single(mine.Functions).Name);
        Assert.Equal(
            Assert.Single(maf.Functions).Name,
            Assert.Single(mine.Functions).Name);
        Assert.Equal("search_widget", Assert.Single(mine.Descriptors).FunctionName);
    }

    /// <summary>
    /// The name-drift invariant both catalogs carry: <c>AIFunctionFactory.Create</c> strips a trailing
    /// "Async" from a <c>Task</c>-returning method with no explicit name, so a descriptor sourcing
    /// <c>FunctionName</c> from <c>method.Name</c> would silently disagree with the name the model
    /// actually sees. Both catalogs must source it from the <c>AIFunction</c>.
    /// </summary>
    [Fact]
    public void AsyncSuffixIsStrippedIdentically_AndTheDescriptorFollowsTheFunction()
    {
        var mine = AffiantToolCatalog.FromType<AsyncTools>();
        var maf = MafCatalog.FromType<AsyncTools>();

        var function = Assert.Single(mine.Functions);
        Assert.Equal("FetchThing", function.Name);
        Assert.Equal(function.Name, Assert.Single(mine.Descriptors).FunctionName);
        Assert.Equal(Assert.Single(maf.Functions).Name, function.Name);
    }

    /// <summary>
    /// An explicit override wins over the derived name even when the method would otherwise have had
    /// its "Async" stripped — the override is the last word, on both backends.
    /// </summary>
    [Fact]
    public void ExplicitOverrideBeatsAsyncStripping_OnBothBackends()
    {
        var mine = Assert.Single(AffiantToolCatalog.FromType<OverriddenAsyncTools>().Functions);
        var maf = Assert.Single(MafCatalog.FromType<MafOverriddenAsyncTools>().Functions);

        Assert.Equal("fetch_thing_async", mine.Name);
        Assert.Equal(maf.Name, mine.Name);
    }

    [Fact]
    public void PluginNameDefaultsToTheToolTypeName_OnBothBackends()
    {
        var mine = Assert.Single(AffiantToolCatalog.FromType<OverriddenTools>().Descriptors);
        var maf = Assert.Single(MafCatalog.FromType<MafOverriddenTools>().Descriptors);

        Assert.Equal(nameof(OverriddenTools), mine.PluginName);
        Assert.Equal(nameof(MafOverriddenTools), maf.PluginName);
    }

    [Fact]
    public void BlankOverride_IsRefusedByBothCatalogs()
    {
        Assert.Throws<InvalidOperationException>(() => AffiantToolCatalog.FromType<BlankOverrideTools>());
        Assert.Throws<InvalidOperationException>(() => MafCatalog.FromType<MafBlankOverrideTools>());
    }

    [Fact]
    public void CollidingEffectiveNames_AreRefusedByBothCatalogs()
    {
        Assert.Throws<InvalidOperationException>(() => AffiantToolCatalog.FromType<CollidingTools>());
        Assert.Throws<InvalidOperationException>(() => MafCatalog.FromType<MafCollidingTools>());
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────
    // Paired per backend wherever the fixture must carry a backend-specific attribute; shared where
    // it need not, so the shared cases prove the two catalogs agree on the very same input type.

    private sealed class ParityTools
    {
        public string ReadWidget() => "widget";

        [AffiantWriteTool("WriteCreate", "Widget", typeof(FakeStrategy))]
        public string CreateWidget(string name) => "created:" + name;
    }

    private sealed class MafParityTools
    {
        public string ReadWidget() => "widget";

        [AffiantWriteTool("WriteCreate", "Widget", typeof(FakeStrategy))]
        public string CreateWidget(string name) => "created:" + name;
    }

    private sealed class OverriddenTools
    {
        [ExtensionsAIToolName("search_widget")]
        public string SearchWidget(string name) => "found:" + name;
    }

    private sealed class MafOverriddenTools
    {
        [MafToolName("search_widget")]
        public string SearchWidget(string name) => "found:" + name;
    }

    private sealed class AsyncTools
    {
        public Task<string> FetchThingAsync() => Task.FromResult("thing");
    }

    private sealed class OverriddenAsyncTools
    {
        [ExtensionsAIToolName("fetch_thing_async")]
        public Task<string> FetchThingAsync() => Task.FromResult("thing");
    }

    private sealed class MafOverriddenAsyncTools
    {
        [MafToolName("fetch_thing_async")]
        public Task<string> FetchThingAsync() => Task.FromResult("thing");
    }

    private sealed class BlankOverrideTools
    {
        [ExtensionsAIToolName("  ")]
        public string DoThing() => "thing";
    }

    private sealed class MafBlankOverrideTools
    {
        [MafToolName("  ")]
        public string DoThing() => "thing";
    }

    private sealed class CollidingTools
    {
        [ExtensionsAIToolName("same_name")]
        public string First() => "1";

        [ExtensionsAIToolName("same_name")]
        public string Second() => "2";
    }

    private sealed class MafCollidingTools
    {
        [MafToolName("same_name")]
        public string First() => "1";

        [MafToolName("same_name")]
        public string Second() => "2";
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";

        public IReadOnlyList<TaskInferenceField> Fields => [];

        public double? MinimumConfidenceThreshold => null;
    }
}
