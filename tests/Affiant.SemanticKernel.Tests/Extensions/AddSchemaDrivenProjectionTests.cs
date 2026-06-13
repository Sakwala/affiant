namespace Affiant.SemanticKernel.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Tests for AddSchemaDrivenProjection&lt;TStrategy&gt;() and the conditional-default behavior
/// in AddAffiantInferenceOrchestration() introduced in Epic 18 story 18.3.
/// </summary>
public class AddSchemaDrivenProjectionTests
{
    // ── Single strategy ───────────────────────────────────────────────────────

    [Fact]
    public void AddSchemaDrivenProjection_SingleStrategy_RegistersProjection()
    {
        var services = BuildBaseServices();
        services.AddSingleton<FakeTaskInferenceStrategy>();
        services.AddSchemaDrivenProjection<FakeTaskInferenceStrategy>();

        var sp = services.BuildServiceProvider();
        var projections = sp.GetServices<IAffidavitProjection>().ToList();

        Assert.Single(projections);
        Assert.IsType<SchemaDrivenAffidavitProjection>(projections[0]);
    }

    // ── Multiple strategies ───────────────────────────────────────────────────

    [Fact]
    public void AddSchemaDrivenProjection_MultipleStrategies_EachRegistersIndependently()
    {
        var services = BuildBaseServices();
        services.AddSingleton<FakeLeaveTaskInferenceStrategy>();
        services.AddSingleton<FakePersonalInfoTaskInferenceStrategy>();
        services.AddSchemaDrivenProjection<FakeLeaveTaskInferenceStrategy>();
        services.AddSchemaDrivenProjection<FakePersonalInfoTaskInferenceStrategy>();

        var sp = services.BuildServiceProvider();
        var projections = sp.GetServices<IAffidavitProjection>().ToList();

        Assert.Equal(2, projections.Count);
        Assert.All(projections, p => Assert.IsType<SchemaDrivenAffidavitProjection>(p));
    }

    // ── Conditional default: no host projections → default applies ────────────

    [Fact]
    public void AddAffiantInferenceOrchestration_NoHostProjections_UsesDefault()
    {
        var services = BuildBaseServices();
        services.AddSingleton<ITaskInferenceStrategy, FakeTaskInferenceStrategy>();
        services.AddAffiantInferenceOrchestration();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var projections = scope.ServiceProvider.GetServices<IAffidavitProjection>().ToList();

        Assert.Single(projections);
        Assert.IsType<SchemaDrivenAffidavitProjection>(projections[0]);
    }

    // ── Conditional default: host projections present → default skipped ───────

    [Fact]
    public void AddAffiantInferenceOrchestration_WithHostProjections_SkipsDefault()
    {
        var services = BuildBaseServices();
        services.AddSingleton<FakeLeaveTaskInferenceStrategy>();
        services.AddSingleton<FakePersonalInfoTaskInferenceStrategy>();
        services.AddSchemaDrivenProjection<FakeLeaveTaskInferenceStrategy>();
        services.AddSchemaDrivenProjection<FakePersonalInfoTaskInferenceStrategy>();
        // AddAffiantInferenceOrchestration finds existing projections and skips the default.
        services.AddAffiantInferenceOrchestration();

        var sp = services.BuildServiceProvider();
        var projections = sp.GetServices<IAffidavitProjection>().ToList();

        Assert.Equal(2, projections.Count);
        Assert.All(projections, p => Assert.IsType<SchemaDrivenAffidavitProjection>(p));
    }

    // ── Backward-compatibility: Meridian single-strategy path ─────────────────

    [Fact]
    public void AddAffiantInferenceOrchestration_MeridianPattern_StillWorks()
    {
        var services = BuildBaseServices();
        services.AddSingleton<ITaskInferenceStrategy, FakeTaskInferenceStrategy>();
        services.AddAffiantInferenceOrchestration();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var projection = scope.ServiceProvider.GetRequiredService<IAffidavitProjection>();

        Assert.NotNull(projection);
        Assert.IsType<SchemaDrivenAffidavitProjection>(projection);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        return services;
    }

    // ── Fake strategies ───────────────────────────────────────────────────────

    private sealed class FakeTaskInferenceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "FakeEntity";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FakeLeaveTaskInferenceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "LeaveRequest";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FakePersonalInfoTaskInferenceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "PersonalInfoUpdate";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }
}
