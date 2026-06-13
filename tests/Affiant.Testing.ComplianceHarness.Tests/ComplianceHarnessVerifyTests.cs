namespace Affiant.Testing.ComplianceHarness.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// ---------------------------------------------------------------------------
// Minimal fake strategies — no real inference logic; used only for type identity
// ---------------------------------------------------------------------------

internal sealed class FakeThingStrategy : ITaskInferenceStrategy
{
    public string EntityName => "Thing";
    public IReadOnlyList<TaskInferenceField> Fields => [];
    public double? MinimumConfidenceThreshold => null;
}

internal sealed class FakePolicyStrategy : ITaskInferenceStrategy
{
    public string EntityName => "Policy";
    public IReadOnlyList<TaskInferenceField> Fields => [];
    public double? MinimumConfidenceThreshold => null;
}

internal sealed class FakeReportStrategy : ITaskInferenceStrategy
{
    public string EntityName => "Report";
    public IReadOnlyList<TaskInferenceField> Fields => [];
    public double? MinimumConfidenceThreshold => null;
}

internal sealed class FakeConcreteStrategy : ITaskInferenceStrategy
{
    public string EntityName => "Concrete";
    public IReadOnlyList<TaskInferenceField> Fields => [];
    public double? MinimumConfidenceThreshold => null;
}

// Fixture matching FakeThingStrategy only
internal sealed class FakeThingComplianceFixture : ITaskInferenceComplianceFixture
{
    public Type Strategy => typeof(FakeThingStrategy);
    public IEnumerable<InferenceFixtureCase> Cases => [];
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class ComplianceHarnessVerifyTests
{
    private static IServiceCollection CreateBase()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        return services;
    }

    [Fact]
    public void Strategy_WithMatchingFixture_Passed_IsTrue()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing");
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeThingComplianceFixture());

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.MissingFixtures);
    }

    [Fact]
    public void Strategy_WithoutMatchingFixture_MissingFixture_Named()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing");

        var result = ComplianceHarness.Verify(services);

        Assert.False(result.Passed);
        var missing = Assert.Single(result.MissingFixtures);
        Assert.Equal(typeof(FakeThingStrategy), missing.StrategyType);
        Assert.Equal("CreateThing", missing.FunctionName);
    }

    // Design-note-2 trap: a concrete-only registration (no ITaskInferenceStrategy binding)
    // is invisible to GetServices<ITaskInferenceStrategy>() but must still be discovered
    // via the registry's descriptor.InferenceStrategy field.
    [Fact]
    public void ConcreteOnlyStrategy_NoInterfaceBinding_StillEnumerated()
    {
        var services = CreateBase();
        services.AddSingleton<FakeConcreteStrategy>(); // concrete-only — no ITaskInferenceStrategy binding
        services.AddAffiantTool<FakeConcreteStrategy>("CreateConcrete", Operation.WriteCreate, "Concrete");
        // no fixture registered

        var result = ComplianceHarness.Verify(services);

        Assert.Contains(result.MissingFixtures, mf => mf.StrategyType == typeof(FakeConcreteStrategy));
    }

    [Fact]
    public void MultipleUnpairedStrategies_AllNamed()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing");
        services.AddAffiantTool<FakePolicyStrategy>("CreatePolicy", Operation.WriteCreate, "Policy");
        services.AddAffiantTool<FakeReportStrategy>("CreateReport", Operation.WriteCreate, "Report");
        // no fixtures

        var result = ComplianceHarness.Verify(services);

        Assert.Equal(3, result.MissingFixtures.Count);
        Assert.Equal(3, result.MissingFixtures.Select(mf => mf.StrategyType).ToHashSet().Count);
    }

    [Fact]
    public void ReadOnlyDescriptor_Ignored()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing");
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeThingComplianceFixture());
        services.AddAffiantReadTool("ReadThings", entityType: "Thing");

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.MissingFixtures);
    }

    // Two descriptors share the same strategy + function name but differ by pluginName
    // (distinct registry keys). A single matching fixture covers both; deduplication
    // on (StrategyType, FunctionName) ensures no spurious missing-fixture entries.
    [Fact]
    public void DuplicateStrategyAndFunction_Deduped()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing", "PluginA");
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing", "PluginB");
        services.AddSingleton<ITaskInferenceComplianceFixture>(new FakeThingComplianceFixture());

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
    }

    [Fact]
    public void EmptyRegistry_Passed_IsTrue()
    {
        var services = CreateBase();

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.MissingFixtures);
    }

    [Fact]
    public void OnlyReadToolsRegistered_Ignored()
    {
        var services = CreateBase();
        services.AddAffiantReadTool("ReadThings", entityType: "Thing");
        services.AddAffiantReadTool("SearchPolicies", entityType: "Policy");

        var result = ComplianceHarness.Verify(services);

        Assert.True(result.Passed);
        Assert.Empty(result.MissingFixtures);
    }
}
