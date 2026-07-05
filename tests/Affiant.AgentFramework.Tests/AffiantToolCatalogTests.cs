namespace Affiant.AgentFramework.Tests;

using System.ComponentModel;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Reflection-pass tests for <see cref="AffiantToolCatalog.FromType{T}"/>: descriptor production
/// (read/write), property-accessor and Object-method exclusion, plugin naming, AIFunction naming
/// via [Description], and per-invocation target resolution from AIFunctionArguments.Services.
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

        // ProbeTools has exactly 2 tool methods (ReadThing, CreateThing) plus one property
        // (Widget) and inherits ToString/Equals/GetHashCode/GetType from object — none of those
        // five extra members may surface as tools.
        Assert.Equal(2, catalog.Descriptors.Count);
        Assert.Equal(2, catalog.Functions.Count);
        Assert.DoesNotContain(catalog.Descriptors, d => d.FunctionName.Contains("Widget"));
        Assert.DoesNotContain(catalog.Descriptors, d => d.FunctionName is "ToString" or "Equals" or "GetHashCode" or "GetType");
    }

    [Fact]
    public void AIFunction_Name_MatchesMethodName_NoAsyncStripping()
    {
        var catalog = AffiantToolCatalog.FromType<ProbeTools>();
        Assert.Contains(catalog.Functions, f => f.Name == "ReadThing");
        Assert.Contains(catalog.Functions, f => f.Name == "CreateThing");
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

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class ProbeTools
    {
        [Description("reads a thing")]
        public string ReadThing() => "read";

        [AffiantWriteTool("WriteCreate", "TestEntity", typeof(FakeStrategy))]
        public string CreateThing(string name) => "created:" + name;

        public string Widget { get; set; } = "unused";
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }
}
