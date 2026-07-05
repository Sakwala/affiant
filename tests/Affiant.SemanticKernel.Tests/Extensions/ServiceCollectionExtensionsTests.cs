namespace Affiant.SemanticKernel.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Connectors;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

/// <summary>
/// Verifies that AddAffiantSemanticKernel() registers all required framework services.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAffiantSemanticKernel_RegistersSemanticKernelOptions_WithDefaults()
    {
        var sp = BuildMinimalProvider();
        var options = sp.GetRequiredService<SemanticKernelOptions>();
        Assert.Equal("AzureOpenAI", options.PrimaryProvider);
        Assert.Equal("Gemini", options.FallbackProvider);
        Assert.True(options.EnableAutoFunctionInvocation);
        Assert.True(options.EnableManualInvocationFallback);
        Assert.Equal(3, options.MaxAutoInvocationRetries);
        Assert.False(options.EnableFilterLogging);
    }

    [Fact]
    public void AddAffiantSemanticKernel_AppliesConfigureCallback()
    {
        var sp = BuildMinimalProvider(configure: opts =>
        {
            opts.PrimaryProvider = "google";
            opts.FallbackProvider = "openai";
            opts.EnableFilterLogging = true;
            opts.MaxAutoInvocationRetries = 5;
        });

        var options = sp.GetRequiredService<SemanticKernelOptions>();
        Assert.Equal("google", options.PrimaryProvider);
        Assert.Equal("openai", options.FallbackProvider);
        Assert.True(options.EnableFilterLogging);
        Assert.Equal(5, options.MaxAutoInvocationRetries);
    }

    [Fact]
    public void AddAffiantSemanticKernel_WorksWithNullConfigure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantSemanticKernel();
        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<SemanticKernelOptions>());
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersCapabilityRegistry()
    {
        var sp = BuildMinimalProvider();
        var registry = sp.GetRequiredService<CapabilityRegistry>();
        Assert.NotNull(registry);
        // Verify the registry resolves known providers
        Assert.True(registry.Resolve("openai").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("google").SupportsAutoFunctionInvocationFilter);
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersManualToolInvoker_AsScoped()
    {
        var sp = BuildMinimalProvider();
        using var scope = sp.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IManualToolInvoker>();
        Assert.NotNull(invoker);
        Assert.IsType<ManualToolInvoker>(invoker);
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersTaskInferenceMergeFilter_AsToolInvocationFilter()
    {
        var sp = BuildProviderWithInferenceStack();
        using var scope = sp.CreateScope();
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();
        Assert.NotEmpty(filters);
        Assert.Single(filters, f => f is TaskInferenceMergeFilter);
    }

    [Fact]
    public void AddAffiantSemanticKernel_RegistersReviewGateFilter_AsToolInvocationFilter()
    {
        var sp = BuildProviderWithInferenceStack();
        using var scope = sp.CreateScope();
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();
        Assert.NotEmpty(filters);
        Assert.Single(filters, f => f is ReviewGateFilter);
    }

    [Fact]
    public void AddAffiantSemanticKernel_FilterPipeline_TaskInferenceMergeBeforeReviewGate()
    {
        // The completion-stage position contract: TaskInferenceMergeFilter (pos 6) must appear
        // before ReviewGateFilter (pos 7) in the registered neutral enumerable — the SK auto-
        // invocation bridge runs the completion-stage filters in that order.
        var sp = BuildProviderWithInferenceStack();
        using var scope = sp.CreateScope();
        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();

        var taskInferenceIdx = filters.FindIndex(f => f is TaskInferenceMergeFilter);
        var reviewGateIdx = filters.FindIndex(f => f is ReviewGateFilter);

        Assert.True(taskInferenceIdx >= 0, "TaskInferenceMergeFilter must be registered");
        Assert.True(reviewGateIdx >= 0, "ReviewGateFilter must be registered");
        Assert.True(taskInferenceIdx < reviewGateIdx,
            "TaskInferenceMergeFilter (pos 6) must precede ReviewGateFilter (pos 7)");
    }

    [Fact]
    public void AddAffiantSemanticKernel_HostRegisteredCapabilityRegistry_TakesPreference()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var hostRegistry = new CapabilityRegistry();
        services.AddSingleton(hostRegistry);         // host registers first
        services.AddAffiantSemanticKernel();         // TryAdd must skip it
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<CapabilityRegistry>();
        Assert.Same(hostRegistry, resolved);
    }

    [Fact]
    public void AddAffiantSemanticKernel_ChainsWith_AddAffiantCore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, StubTaskInferenceStrategy>();
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel(opts => opts.PrimaryProvider = "openai");
        services.AddAffiantCore(opts => opts.EnableObservability = false);

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<SemanticKernelOptions>());
        Assert.NotNull(sp.GetRequiredService<CapabilityRegistry>());
        Assert.NotNull(sp.GetRequiredService<AffiantCoreOptions>());
    }

    [Fact]
    public void AddAffiantSemanticKernel_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddAffiantSemanticKernel();
        Assert.Same(services, returned);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ServiceProvider BuildMinimalProvider(
        Action<SemanticKernelOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantSemanticKernel(configure);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProviderWithInferenceStack()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskInferenceStrategy, StubTaskInferenceStrategy>();
        services.AddSingleton<IAffiantToolRegistry>(new AffiantToolRegistry());
        services.AddScoped<ContextFabric>();
        services.AddScoped<TaskInferenceStep>();
        services.AddAffiantSemanticKernel();
        return services.BuildServiceProvider();
    }

    private sealed class StubTaskInferenceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "StubEntity";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }
}
