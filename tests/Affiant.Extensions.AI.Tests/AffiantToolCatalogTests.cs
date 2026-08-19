namespace Affiant.Extensions.AI.Tests;

using System.ComponentModel;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Extensions.AI.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Reflection-pass tests for this package's <see cref="AffiantToolCatalog.FromType{T}"/>: descriptor
/// production (read/write), property-accessor and Object-method exclusion, plugin naming,
/// <see cref="DescriptionAttribute"/>-derived function descriptions, per-invocation target resolution
/// from <see cref="AIFunctionArguments.Services"/>, and the three refusals the catalog makes at build
/// time.
///
/// <para>
/// Deliberately the same test set, in the same order, as
/// <c>tests/Affiant.AgentFramework.Tests/AffiantToolCatalogTests.cs</c> — this catalog is a copy of
/// that one (design brief decision 3, "copy, don't reference": an adapter-to-adapter ProjectReference
/// would amend the layering invariant Area-8 just re-established, so the consolidation waits for
/// post-beta). Two files that must not drift should be readable side by side, and a case covered on
/// one backend but not the other is exactly how a copy starts drifting.
/// <c>Extensions/CatalogReflectionParityTests</c> holds the two catalogs to identical
/// <em>output</em>; this file holds this one to the documented behaviour on its own terms, so a
/// regression that broke both copies the same way still fails something.
/// </para>
/// </summary>
public class AffiantToolCatalogTests
{
    [Fact]
    public void ReadMethod_ProducesReadQueryDescriptor()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();

        var descriptor = Assert.Single(catalog.Descriptors, d => d.FunctionName == "ReadThing");
        Assert.Equal(Operation.ReadQuery, descriptor.Operation);
        Assert.Null(descriptor.EntityType);
        Assert.Null(descriptor.InferenceStrategy);
        Assert.Equal("ProbeTools", descriptor.PluginName);
    }

    [Fact]
    public void WriteAttribute_ProducesWriteDescriptor()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();

        var descriptor = Assert.Single(catalog.Descriptors, d => d.FunctionName == "CreateThing");
        Assert.Equal("WriteCreate", descriptor.Operation.Kind);
        Assert.Equal("TestEntity", descriptor.EntityType);
        Assert.Equal(typeof(FakeStrategy), descriptor.InferenceStrategy);
    }

    [Fact]
    public void PluginName_DefaultsToTypeName()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();
        Assert.All(catalog.Descriptors, d => Assert.Equal("ProbeTools", d.PluginName));
    }

    [Fact]
    public void PluginName_HonorsExplicitValue()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>(pluginName: "CustomPlugin");
        Assert.All(catalog.Descriptors, d => Assert.Equal("CustomPlugin", d.PluginName));
    }

    [Fact]
    public void PropertyAccessorsAndObjectMethods_AreExcluded()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();

        // ProbeTools has exactly 2 tool methods (ReadThing, CreateThing) plus one property (Widget)
        // and inherits ToString/Equals/GetHashCode/GetType from object — none of those five extra
        // members may surface as tools.
        Assert.Equal(2, catalog.Descriptors.Count);
        Assert.Equal(2, catalog.Functions.Count);
        Assert.DoesNotContain(catalog.Descriptors, d => d.FunctionName.Contains("Widget"));
        Assert.DoesNotContain(catalog.Descriptors,
            d => d.FunctionName is "ToString" or "Equals" or "GetHashCode" or "GetType");
    }

    [Fact]
    public void AIFunction_Name_MatchesMethodName_ForPlainSyncMethods()
    {
        // The trivial no-op case only: ReadThing/CreateThing are synchronous, non-Task-returning and
        // don't end in "Async", so AIFunctionFactory.Create passes their names through unchanged.
        // The two tests below cover the cases where it does not.
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();
        Assert.Contains(catalog.Functions, f => f.Name == "ReadThing");
        Assert.Contains(catalog.Functions, f => f.Name == "CreateThing");
    }

    [Fact]
    public void AIFunction_Name_SyncMethod_AsyncSuffixIsNotStripped()
    {
        // AIFunctionFactory.Create's trailing-"Async" stripping is conditioned on the return type
        // (Task/ValueTask/IAsyncEnumerable), not on the method name — a synchronous method that
        // happens to end in "Async" keeps the literal name.
        var catalog = AffiantToolCatalog.FromType<SyncAsyncSuffixedTools>();

        var fn = Assert.Single(catalog.Functions);
        Assert.Equal("LookupThingAsync", fn.Name);

        var descriptor = Assert.Single(catalog.Descriptors);
        Assert.Equal("LookupThingAsync", descriptor.FunctionName);
    }

    [Fact]
    public void NoOverride_AsyncSuffixedTaskReturningMethod_DescriptorMirrorsSanitizedAIFunctionName()
    {
        // The invariant the catalog's own comment states: AffiantToolDescriptor.FunctionName always
        // equals the AIFunction's actual, LLM-visible name — including on the no-override path, where
        // AIFunctionFactory.Create strips the trailing "Async" from a Task-returning method. Sourcing
        // the descriptor from method.Name instead would record "FetchThingAsync" while every
        // invocation and the model itself carry "FetchThing" (the affiant#16 silent-name-drift bug,
        // inherited here with the copy).
        var catalog = AffiantToolCatalog.FromType<AsyncSuffixedTaskTools>();

        var fn = Assert.Single(catalog.Functions);
        Assert.Equal("FetchThing", fn.Name);

        var descriptor = Assert.Single(catalog.Descriptors);
        Assert.Equal("FetchThing", descriptor.FunctionName);
        Assert.Equal(fn.Name, descriptor.FunctionName);
    }

    [Fact]
    public void AIFunction_Description_HonorsDescriptionAttribute()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();
        var fn = catalog.Functions.Single(f => f.Name == "ReadThing");
        Assert.Equal("reads a thing", fn.Description);
    }

    [Fact]
    public async Task AIFunction_ResolvesTargetFromServicesAtInvocationTime()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();
        var fn = catalog.Functions.Single(f => f.Name == "CreateThing");

        var services = new ServiceCollection();
        services.AddSingleton(new ProbeTools());
        var provider = services.BuildServiceProvider();

        var args = new AIFunctionArguments { ["name"] = "widget", Services = provider };
        var result = await fn.InvokeAsync(args);

        Assert.Equal("created:widget", result?.ToString()?.Trim('"'));
    }

    [Fact]
    public async Task AIFunction_Throws_WhenTargetNotRegistered()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();
        var fn = catalog.Functions.Single(f => f.Name == "ReadThing");

        var provider = new ServiceCollection().BuildServiceProvider();
        var args = new AIFunctionArguments { Services = provider };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fn.InvokeAsync(args).AsTask());
    }

    [Fact]
    public void OverloadedToolMethod_ThrowsAtCatalogBuildTime_WithClearMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AffiantToolCatalog.FromType<OverloadedTools>());

        Assert.Contains("DoThing", ex.Message);
        Assert.Contains("overload", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rename", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── [AffiantToolName] override ───────────────────────────────────────────

    [Fact]
    public void AffiantToolName_Override_BecomesAIFunctionName()
    {
        var catalog = AffiantToolCatalog.FromType<OverrideTools>();

        Assert.Contains(catalog.Functions, f => f.Name == "search_aircraft");
        Assert.DoesNotContain(catalog.Functions, f => f.Name == "SearchAircraft");
    }

    [Fact]
    public void AffiantToolName_Override_FlowsIntoDescriptorFunctionName()
    {
        var catalog = AffiantToolCatalog.FromType<OverrideTools>();

        var descriptor = Assert.Single(catalog.Descriptors, d => d.FunctionName == "search_aircraft");
        Assert.Equal("WriteCreate", descriptor.Operation.Kind);
        Assert.DoesNotContain(catalog.Descriptors, d => d.FunctionName == "SearchAircraft");
    }

    [Fact]
    public void NoAffiantToolName_MethodNameUnaffectedByFeatureExisting()
    {
        // Regression guard: a method with no [AffiantToolName] on a type that also has overridden
        // methods must still surface under its default resolved name.
        var catalog = AffiantToolCatalog.FromType<OverrideTools>();

        Assert.Contains(catalog.Functions, f => f.Name == "PlainRead");
    }

    [Fact]
    public async Task AffiantToolName_Override_DoesNotAffectInvocationOrTargetResolution()
    {
        var catalog = AffiantToolCatalog.FromType<OverrideTools>();
        var fn = catalog.Functions.Single(f => f.Name == "search_aircraft");

        var services = new ServiceCollection();
        services.AddSingleton(new OverrideTools());
        var provider = services.BuildServiceProvider();

        var args = new AIFunctionArguments { ["tailNumber"] = "N12345", Services = provider };
        var result = await fn.InvokeAsync(args);

        Assert.Equal("created:N12345", result?.ToString()?.Trim('"'));
    }

    [Fact]
    public void AffiantToolName_BlankOverride_ThrowsAtCatalogBuildTime_WithClearMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AffiantToolCatalog.FromType<BlankOverrideTools>());

        Assert.Contains("BlankName", ex.Message);
        Assert.Contains("AffiantToolName", ex.Message);
    }

    [Fact]
    public void AffiantToolName_CollidingOverride_ThrowsAtCatalogBuildTime_WithClearMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AffiantToolCatalog.FromType<CollidingOverrideTools>());

        // Reflection method order is not contractually guaranteed, so assert only on the colliding
        // effective name and the class of problem — never on which method is reported as the earlier.
        Assert.Contains("shared_name", ex.Message);
        Assert.Contains("collides", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class OverloadedTools
    {
        public string DoThing() => "no-arg";
        public string DoThing(string value) => value;
    }

    private sealed class ProbeTools
    {
        [Description("reads a thing")]
        public string ReadThing() => "read";

        [AffiantWriteTool("WriteCreate", "TestEntity", typeof(FakeStrategy))]
        public string CreateThing(string name) => "created:" + name;

        public string Widget { get; set; } = "unused";
    }

    private sealed class OverrideTools
    {
        [AffiantToolName("search_aircraft")]
        [AffiantWriteTool("WriteCreate", "TestEntity", typeof(FakeStrategy))]
        public string SearchAircraft(string tailNumber) => "created:" + tailNumber;

        public string PlainRead() => "read";
    }

    private sealed class BlankOverrideTools
    {
        [AffiantToolName("   ")]
        public string BlankName() => "unused";
    }

    private sealed class AsyncSuffixedTaskTools
    {
        public Task<string> FetchThingAsync() => Task.FromResult("fetched");
    }

    private sealed class SyncAsyncSuffixedTools
    {
        public string LookupThingAsync() => "looked-up";
    }

    private sealed class CollidingOverrideTools
    {
        // No override — the bare C# name is already "shared_name".
        public string shared_name() => "one";

        // Overridden to the same effective name as the method above — must collide.
        [AffiantToolName("shared_name")]
        public string MethodTwo() => "two";
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }
}
