namespace Affiant.Extensions.AI.Tests.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Affiant.Core.Validation;
using Affiant.Extensions.AI.Adapters;
using Affiant.Extensions.AI.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// What <c>AddAffiantExtensionsAI</c> puts in the container: this adapter's inference port and
/// task-inference orchestration, and neutral filter positions 4–7 in the canonical order (framework
/// spec §3.12.4). Microsoft.Extensions.AI has one function-calling seam — unlike Semantic Kernel's
/// invocation/auto-invocation split — so all four filters run at the single seam
/// <c>AffiantDelegatingAIFunction</c> fires, and their registration order here <em>is</em> their
/// onion order.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    /// <summary>
    /// The onion, outermost first. Positions 1–3 come from <c>AddAffiantCore</c>; 4–7 from this
    /// adapter. Pinned as an exact sequence rather than as pairwise "before" assertions because the
    /// order is the contract: <c>ReviewGateFilter</c> sits outside <c>TaskInferenceMergeFilter</c> so
    /// the merge's post-processing runs before the gate's, which is what lets the gate file a
    /// proposal that already carries the inferred fields.
    /// </summary>
    [Fact]
    public void RegistersNeutralFilters_InCanonicalOnionOrder()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();

        var filters = scope.ServiceProvider
            .GetServices<IToolInvocationFilter>()
            .Select(f => f.GetType())
            .ToList();

        Assert.Equal(
            [
                typeof(ToolErrorFilter),
                typeof(DeterministicShortCircuit),
                typeof(ToolTracingFilter),
                typeof(ToolArgumentCaptureFilter),
                typeof(InferenceTriggerFilter),
                typeof(ReviewGateFilter),
                typeof(TaskInferenceMergeFilter),
            ],
            filters);
    }

    [Fact]
    public void RegistersTheExtensionsAIInferencePort()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();

        Assert.IsType<ExtensionsAIInferenceCompletionPort>(
            scope.ServiceProvider.GetRequiredService<IInferenceCompletionPort>());
    }

    [Fact]
    public void RegistersTaskInferenceOrchestration()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TaskInferenceRunner>());
        Assert.NotEmpty(scope.ServiceProvider.GetServices<IInferenceTrigger>());
    }

    /// <summary>
    /// Asserted against the descriptors rather than by resolving: the default
    /// <c>SchemaDrivenAffidavitProjection</c> takes the write tool's
    /// <see cref="ITaskInferenceStrategy"/>, which only a host registering a real write tool
    /// supplies. What this adapter owes is the registration, and only when no host projection
    /// already exists.
    /// </summary>
    [Fact]
    public void RegistersTheDefaultAffidavitProjection_OnlyWhenTheHostHasNone()
    {
        var services = NewServices();
        services.AddAffiantExtensionsAI();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IAffidavitProjection) &&
            sd.ImplementationType == typeof(SchemaDrivenAffidavitProjection));

        var withHostProjection = NewServices();
        withHostProjection.AddSingleton<IAffidavitProjection>(new HostProjection());
        withHostProjection.AddAffiantExtensionsAI();

        Assert.DoesNotContain(withHostProjection, sd =>
            sd.ServiceType == typeof(IAffidavitProjection) &&
            sd.ImplementationType == typeof(SchemaDrivenAffidavitProjection));
    }

    [Fact]
    public void AppliesTheConfigureCallback()
    {
        using var sp = BuildProvider(opts => opts.AcknowledgeUncoveredTools = ["web_search"]);

        Assert.Equal(
            ["web_search"],
            sp.GetRequiredService<ExtensionsAIOptions>().AcknowledgeUncoveredTools);
    }

    [Fact]
    public void WorksWithNoConfigureCallback()
    {
        using var sp = BuildProvider();

        Assert.Empty(sp.GetRequiredService<ExtensionsAIOptions>().AcknowledgeUncoveredTools);
    }

    /// <summary>
    /// Every registration but the options object goes through <c>TryAdd</c>/<c>TryAddEnumerable</c>,
    /// so a host that calls the method twice (a common copy-paste in a composition root split across
    /// files) gets one filter chain, not two — which would run the non-idempotent neutral onion twice
    /// per tool call, the same corruption the double-wrap guard exists to prevent.
    /// </summary>
    [Fact]
    public void IsIdempotent_AcrossRepeatedCalls()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(new UnusedChatClient());
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        services.AddAffiantExtensionsAI();
        services.AddAffiantExtensionsAI();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var filters = scope.ServiceProvider.GetServices<IToolInvocationFilter>().ToList();
        Assert.Equal(filters.Count, filters.Select(f => f.GetType()).Distinct().Count());
    }

    /// <summary>
    /// The Area-8 startup wire-up validator applies to this backend unchanged, and that is the whole
    /// integration: it asks the container which contracts are registered, a question with no
    /// backend-specific answer, so this adapter adds nothing to it and must not shadow it either.
    /// Asserted here so a future edit that starts registering hosted services cannot drop it.
    /// </summary>
    [Fact]
    public void LeavesTheCoreWireUpValidatorInPlace()
    {
        using var sp = BuildProvider();

        Assert.Single(sp.GetServices<IHostedService>().OfType<AffiantWireUpValidator>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ServiceProvider BuildProvider(Action<ExtensionsAIOptions>? configure = null)
    {
        var services = NewServices();
        services.AddAffiantExtensionsAI(configure);
        return services.BuildServiceProvider();
    }

    private static IServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(new UnusedChatClient());
        services.AddAffiantCore(opts => opts.EnableObservability = false);
        return services;
    }

    private sealed class HostProjection : IAffidavitProjection
    {
        public string EntityType => "Widget";

        public Affidavit Project(
            IContextFabric fabric, string operationType, IReadOnlyList<string> warnings, string? entityId = null)
            => throw new InvalidOperationException("HostProjection.Project should not be called");
    }

    /// <summary>
    /// The port takes an <see cref="IChatClient"/> at construction; these tests resolve services but
    /// never run an inference, so any call is a defect and says so.
    /// </summary>
    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("UnusedChatClient.GetResponseAsync should not be called");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("UnusedChatClient.GetStreamingResponseAsync should not be called");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
