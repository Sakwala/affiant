namespace Affiant.SemanticKernel.Tests.Integration;

using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Filters;
using Affiant.TestInfrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenTelemetry.Trace;

/// <summary>
/// Shared scaffolding for InferenceFailSafeIntegrationTests and InferenceIdempotencyIntegrationTests.
/// Builds a real Kernel + ServiceCollection with the full L2 pipeline wired (AddAffiantCore →
/// AddAffiantInferenceOrchestration → AddAffiantSkFilters), the InMemoryExporterHelper wired,
/// and the inference port replaced with a RecordingInferencePort stub.
///
/// Tests drive kernel.InvokeAsync (NOT a synthesized AutoFunctionInvocationContext) so the full
/// IFunctionInvocationFilter chain fires: ToolErrorFilter → DeterministicShortCircuit →
/// ToolTracingFilter → ToolArgumentCaptureFilter → InferenceTriggerFilter.
///
/// Note: KernelPluginFactory.CreateFromFunctions does NOT exercise the [AffiantWriteTool]
/// attribute walker (AddAffiantPluginsFromAssembly). Tool descriptors are registered manually
/// via AddAffiantTool<> with pluginName matching the plugin registered below — this is the
/// same shortcut the 16.8 closure test takes.
/// </summary>
internal static class IntegrationTestPipelineFactory
{
    /// <summary>
    /// ActivitySource for test root spans. Registered with the TracerProvider so that
    /// activities started on this source are captured and establish a TraceId that all
    /// child activities (SK function invocations) inherit. Tests use this TraceId to
    /// filter ExportedActivities to their own invocations, excluding emissions from
    /// other test assemblies running concurrently on the shared Affiant.TaskInference source.
    /// </summary>
    internal static readonly ActivitySource TestActivitySource =
        new("Affiant.SemanticKernel.Tests.Integration");

    public static (Kernel Kernel, InMemoryExporterHelper Exporter, RecordingInferencePort Port) BuildPipeline(
        Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> portImpl,
        IEnumerable<IInferenceTrigger>? additionalTriggers = null,
        bool registerSecondTool = false)
    {
        var recordingPort = new RecordingInferencePort(portImpl);
        var exporter = new InMemoryExporterHelper();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore();

        // Register the recording port BEFORE AddAffiantInferenceOrchestration so that
        // TryAddScoped for IInferenceCompletionPort is a no-op — our singleton wins.
        services.AddSingleton<IInferenceCompletionPort>(recordingPort);

        services.AddAffiantInferenceOrchestration();
        services.AddAffiantSkFilters();

        // Tool descriptors with pluginName = "ThingPlugin" so the registry Find(functionName, pluginName)
        // exact-key match succeeds when InferenceTriggerFilter queries with context.Function.PluginName.
        services.AddAffiantTool<FakeThingStrategy>("CreateThing", Operation.WriteCreate, "Thing", pluginName: "ThingPlugin");
        if (registerSecondTool)
            services.AddAffiantTool<FakeOtherThingStrategy>("CreateOtherThing", Operation.WriteCreate, "OtherThing", pluginName: "ThingPlugin");

        // Story 20.3: TaskInferenceStep no longer takes ITaskInferenceStrategy in its constructor.
        // This registration is kept for tests that resolve ITaskInferenceStrategy from DI directly
        // (e.g. pipeline-order assertions), but is not required for TaskInferenceStep itself.
        services.AddSingleton<ITaskInferenceStrategy>(sp => sp.GetRequiredService<FakeThingStrategy>());

        // Additional triggers: tests that register extra IInferenceTrigger instances (e.g. AlwaysTrueTrigger)
        // add them on top of the WriteIntentInferenceTrigger registered by AddAffiantInferenceOrchestration.
        if (additionalTriggers is not null)
            foreach (var t in additionalTriggers)
                services.AddSingleton<IInferenceTrigger>(t);

        // OTel setup: RegisterWithServices adds the in-memory exporter; the second call
        // subscribes to Affiant activity sources so the TracerProvider samples them.
        // Multiple AddOpenTelemetry().WithTracing() calls on the same IServiceCollection
        // are cumulative — both exporter and source subscriptions apply to the same provider.
        exporter.RegisterWithServices(services);
        services.AddOpenTelemetry()
            .WithTracing(builder => builder
                .AddSource(AffiantTelemetry.AffiantActivitySource.Name)
                .AddSource(AffiantTelemetry.AffiantTaskInferenceActivitySource.Name)
                .AddSource(TestActivitySource.Name));

        // Register Kernel in DI so filters resolve through the same ServiceProvider as the rest.
        services.AddKernel();

        var sp = services.BuildServiceProvider();

        // Force-build the TracerProvider to activate the in-memory exporter.
        // In non-hosted test environments IHostedService is not started automatically;
        // resolving TracerProvider directly triggers provider construction and registers
        // the global ActivityListener that captures Affiant.Framework + Affiant.TaskInference spans.
        // If TracerProvider is not registered as a direct service, fall back to starting hosted services.
        var tracerProvider = sp.GetService<TracerProvider>();
        if (tracerProvider is null)
        {
            foreach (var svc in sp.GetServices<IHostedService>())
                svc.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        // Use a scope so scoped services (filters, runner) resolve with per-request lifetime.
        var scope = sp.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();

        var functions = new List<KernelFunction>
        {
            KernelFunctionFactory.CreateFromMethod(
                () => """{"$type":"WriteProposal","Proposed":{}}""", "CreateThing"),
        };
        if (registerSecondTool)
            functions.Add(KernelFunctionFactory.CreateFromMethod(
                () => """{"$type":"WriteProposal","Proposed":{}}""", "CreateOtherThing"));

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("ThingPlugin", functions));

        // Set conversation context required by InferenceTriggerFilter's idempotency bookkeeping.
        // kernel.Data["ConversationId"] provides the conversation-level dedup key.
        // kernel.Data["AffiantTurnNumber"] provides the turn-level dedup key.
        // kernel.Data["ChatHistory"] is read by the port to construct the inference prompt.
        kernel.Data["ConversationId"] = "test-conv-001";
        kernel.Data["AffiantTurnNumber"] = 0;
        kernel.Data["ChatHistory"] = new ChatHistory();

        return (kernel, exporter, recordingPort);
    }

    /// <summary>
    /// A JSON object whose "Title" property matches FakeThingStrategy.Fields,
    /// so TaskInferenceStep merges exactly one field with confidence 0.9.
    /// </summary>
    public static readonly JsonElement SampleInferenceJson =
        JsonDocument.Parse("""{"Title":{"value":"X","confidence":0.9}}""").RootElement.Clone();

    // ── Inner types ───────────────────────────────────────────────────────────

    public sealed class RecordingInferencePort : IInferenceCompletionPort
    {
        private readonly Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> _impl;
        private int _invocationCount;

        public int InvocationCount => _invocationCount;

        public RecordingInferencePort(Func<InferenceCompletionRequest, CancellationToken, Task<JsonElement>> impl)
            => _impl = impl;

        public async Task<JsonElement> CompleteStructuredAsync(
            InferenceCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return await _impl(request, cancellationToken);
        }
    }

    public sealed class FakeThingStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
            [new TaskInferenceField("Title", "string", "Title of the thing")];
        public double? MinimumConfidenceThreshold => null;
    }

    public sealed class FakeOtherThingStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "OtherThing";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
            [new TaskInferenceField("Name", "string", "Name of the other thing")];
        public double? MinimumConfidenceThreshold => null;
    }
}
