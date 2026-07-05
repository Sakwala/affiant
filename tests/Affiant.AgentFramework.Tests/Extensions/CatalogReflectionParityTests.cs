namespace Affiant.AgentFramework.Tests.Extensions;

using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.SemanticKernel.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

/// <summary>
/// Parity check: for an equivalent tool type, <see cref="AffiantToolCatalog.FromType{T}"/> (MAF)
/// must register the same <see cref="AffiantToolDescriptor"/> set as
/// <c>Affiant.SemanticKernel.Extensions.KernelBuilderExtensions.AddAffiantPluginsFromType{T}</c>
/// (SK) — same function names, plugin name, operation, entity type, and inference strategy.
/// The fixture type below avoids the one documented naming asymmetry between backends (SK strips
/// a bare trailing "Async" from [KernelFunction] methods with no explicit name; MAF's
/// AIFunctionFactory does not) by using method names with no "Async" suffix.
/// </summary>
public class CatalogReflectionParityTests
{
    [Fact]
    public void FromType_ProducesSameDescriptorSet_AsSkPluginWalker()
    {
        var mafDescriptors = AffiantToolCatalog.FromType<ParityTools>().Descriptors;

        var services = new ServiceCollection();
        services.AddAffiantCore();
        var builder = Kernel.CreateBuilder();
        foreach (var sd in services) builder.Services.Add(sd);
        builder.AddAffiantPluginsFromType<ParityTools>();
        var registry = builder.Services.BuildServiceProvider().GetRequiredService<IAffiantToolRegistry>();
        var skDescriptors = registry.All;

        Assert.Equal(skDescriptors.Count, mafDescriptors.Count);

        foreach (var skDescriptor in skDescriptors)
        {
            var mafDescriptor = Assert.Single(mafDescriptors, d => d.FunctionName == skDescriptor.FunctionName);
            Assert.Equal(skDescriptor.PluginName, mafDescriptor.PluginName);
            Assert.Equal(skDescriptor.Operation, mafDescriptor.Operation);
            Assert.Equal(skDescriptor.EntityType, mafDescriptor.EntityType);
            Assert.Equal(skDescriptor.InferenceStrategy, mafDescriptor.InferenceStrategy);
        }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class ParityTools
    {
        [KernelFunction]
        public string ReadWidget() => "widget";

        [KernelFunction]
        [AffiantWriteTool("WriteCreate", "Widget", typeof(FakeStrategy))]
        public string CreateWidget(string name) => "created:" + name;
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }
}
