namespace Affiant.SemanticKernel.Tests.Extensions;

using System.Reflection;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.SemanticKernel.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

public class AddAffiantPluginsFromAssemblyTests
{
    [Fact]
    public void WriteMethod_WithAffiantWriteToolAttribute_RegistersDescriptorWithStrategyType()
    {
        var sp = BuildHost(TestAssembly, "TestPlugin");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("CreateThing", "TestPlugin");

        Assert.NotNull(descriptor);
        Assert.Equal("WriteCreate", descriptor.Operation.Kind);
        Assert.Equal("Thing", descriptor.EntityType);
        Assert.Equal(typeof(FakeStrategy), descriptor.InferenceStrategy);
        Assert.Equal("TestPlugin", descriptor.PluginName);
    }

    [Fact]
    public void ReadMethod_WithoutAffiantWriteToolAttribute_RegistersReadQueryDescriptor()
    {
        var sp = BuildHost(TestAssembly, "TestPlugin");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("FindThings", "TestPlugin");

        Assert.NotNull(descriptor);
        Assert.Equal("ReadQuery", descriptor.Operation.Kind);
        Assert.Null(descriptor.EntityType);
        Assert.Null(descriptor.InferenceStrategy);
    }

    [Fact]
    public void BothMethodsLand_InRegistry()
    {
        var sp = BuildHost(TestAssembly, "TestPlugin");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        // Assert by Find — test assembly has three [KernelFunction] methods, not two.
        var read = registry.Find("FindThings", "TestPlugin");
        var write = registry.Find("CreateThing", "TestPlugin");

        Assert.NotNull(read);
        Assert.NotNull(write);
    }

    [Fact]
    public void PluginNameParameter_AppliesToAllDescriptors_FromThatCall()
    {
        var sp = BuildHost(TestAssembly, "MyPlugin");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var read = registry.Find("FindThings", "MyPlugin");
        var write = registry.Find("CreateThing", "MyPlugin");

        Assert.NotNull(read);
        Assert.Equal("MyPlugin", read.PluginName);
        Assert.NotNull(write);
        Assert.Equal("MyPlugin", write.PluginName);
    }

    [Fact]
    public void MethodWithAffiantWriteToolButNoKernelFunction_IsSilentlySkipped()
    {
        var sp = BuildHost(TestAssembly, "TestPlugin");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        // NotAKernelFunction has [AffiantWriteTool] but no [KernelFunction] — must not appear.
        var descriptor = registry.Find("NotAKernelFunction", "TestPlugin");

        Assert.Null(descriptor);
    }

    [Fact]
    public void AttributeRoundTrip_ParametersPreservedThroughReflection()
    {
        // Round-trip absorbed here per merge justification (PRD Task 3 §Verification).
        var method = typeof(FakeWriteTool).GetMethod("CreateThing");
        Assert.NotNull(method);

        var attr = method.GetCustomAttribute<AffiantWriteToolAttribute>();

        Assert.NotNull(attr);
        Assert.Equal("WriteCreate", attr.Operation);
        Assert.Equal("Thing", attr.EntityType);
        Assert.Equal(typeof(FakeStrategy), attr.InferenceStrategy);
    }

    [Fact]
    public void HostDefinedOperationKind_RoundTripsThroughWalker()
    {
        // "WriteUpsert" is host-defined — locks the open-record extensibility contract from 15.1.
        var sp = BuildHost(TestAssembly, "TestPlugin");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("UpsertThing", "TestPlugin");

        Assert.NotNull(descriptor);
        Assert.Equal("WriteUpsert", descriptor.Operation.Kind);
        Assert.Equal("Thing", descriptor.EntityType);
        Assert.Equal(typeof(FakeStrategy), descriptor.InferenceStrategy);
    }

    private static readonly Assembly TestAssembly = typeof(AddAffiantPluginsFromAssemblyTests).Assembly;

    // Fresh provider per call — registry accumulates; reuse would cause duplicate-key failures.
    private static IServiceProvider BuildHost(Assembly assembly, string? pluginName = "TestPlugin")
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();

        var builder = Kernel.CreateBuilder();

        // Bridge mirrors how a real ASP.NET Core host shares one IServiceCollection with the kernel.
        foreach (var sd in services)
            builder.Services.Add(sd);

        builder.AddAffiantPluginsFromAssembly(assembly, pluginName);

        return builder.Services.BuildServiceProvider();
    }

    private sealed class FakeReadOnlyTool
    {
        [KernelFunction("FindThings")]
        public Task<string> FindThings(string query) => Task.FromResult("[]");
    }

    private sealed class FakeWriteTool
    {
        [KernelFunction("CreateThing")]
        [AffiantWriteTool("WriteCreate", "Thing", typeof(FakeStrategy))]
        public Task<string> CreateThing(string name) => Task.FromResult("{}");
    }

    private sealed class FakeHostDefinedKindTool
    {
        [KernelFunction("UpsertThing")]
        [AffiantWriteTool("WriteUpsert", "Thing", typeof(FakeStrategy))]
        public Task<string> UpsertThing(string name) => Task.FromResult("{}");
    }

    private sealed class FakeAttributeWithoutKernelFunction
    {
        [AffiantWriteTool("WriteCreate", "Thing", typeof(FakeStrategy))]
        public Task<string> NotAKernelFunction() => Task.FromResult("{}");
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }
}
