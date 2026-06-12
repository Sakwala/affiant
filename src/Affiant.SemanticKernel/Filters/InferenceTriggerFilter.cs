namespace Affiant.SemanticKernel.Filters;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

/// <summary>
/// Pre-tool IFunctionInvocationFilter that fires structured-output inference before a registered
/// write-intent tool executes. For each <see cref="IInferenceTrigger"/> that returns true,
/// resolves the tool's <see cref="ITaskInferenceStrategy"/> and invokes <see cref="TaskInferenceRunner"/>.
///
/// Four-step algorithm per PRD §3.2:
///   1. Trigger evaluation — short-circuits on first true.
///   2. Idempotency check — once per (ConversationId, FunctionName, TurnNumber).
///      Bookkeeping anchored on IContextFabric via reserved entity key "inference_idempotency".
///   3. Strategy resolution — from IAffiantToolRegistry + IServiceProvider.
///   4. Run inference — fail-safe: any non-cancellation exception logs warning + continues.
///   5. Tool call — next(context) always fires in every path.
///
/// NOTE: This filter implements IFunctionInvocationFilter (pre-tool), NOT IAutoFunctionInvocationFilter.
/// Per PRD §3.2 first paragraph.
/// </summary>
public sealed class InferenceTriggerFilter : IFunctionInvocationFilter
{
    // Reserved key under which the idempotency tracker entity is stored in IContextFabric.
    private const string IdempotencyEntityId = "inference_idempotency";

    private readonly IEnumerable<IInferenceTrigger> _triggers;
    private readonly TaskInferenceRunner _runner;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAffiantToolRegistry _registry;
    private readonly ILogger<InferenceTriggerFilter> _logger;

    public InferenceTriggerFilter(
        IEnumerable<IInferenceTrigger> triggers,
        TaskInferenceRunner runner,
        IServiceProvider serviceProvider,
        IAffiantToolRegistry registry,
        ILogger<InferenceTriggerFilter> logger)
    {
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        // IContextFabric resolved from DI — Singleton (same fabric anchors idempotency bookkeeping).
        var fabric = _serviceProvider.GetRequiredService<IContextFabric>();

        // Step 1: Trigger evaluation — short-circuit on first true trigger.
        // KernelArguments implements IDictionary but not IReadOnlyDictionary; copy to concrete Dictionary.
        IReadOnlyDictionary<string, object?> args = context.Arguments is not null
            ? context.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value)
            : new Dictionary<string, object?>(0);

        var triggerCtx = new InferenceTriggerContext(
            FunctionName: context.Function.Name,
            PluginName: context.Function.PluginName,
            Arguments: args,
            Fabric: fabric,
            Phase: InferencePhase.PreTool);

        var shouldRun = false;
        foreach (var trigger in _triggers)
        {
            if (trigger.ShouldRun(triggerCtx))
            {
                shouldRun = true;
                break;
            }
        }

        if (!shouldRun)
        {
            // Emit inference.skipped when a descriptor exists so the host knows the filter
            // evaluated this function and chose not to run inference.
            var skipDescriptor = _registry.Find(context.Function.Name, context.Function.PluginName);
            if (skipDescriptor is not null)
            {
                Activity.Current?.AddEvent(new ActivityEvent(
                    "inference.skipped",
                    tags: new ActivityTagsCollection
                    {
                        { L2TelemetryKeys.FunctionName, context.Function.Name },
                        { L2TelemetryKeys.SkipReason, "not_a_write_tool" },
                    }));
            }
            await next(context);
            return;
        }

        // Step 2: Idempotency check — (ConversationId, FunctionName, TurnNumber).
        // ConversationId from kernel.Data["ConversationId"]; fall back to fabric instance hash.
        // TurnNumber from kernel.Data["AffiantTurnNumber"]; fall back to 0 (more conservative dedup).
        var conversationId = GetConversationId(context.Kernel, fabric);
        var turnNumber = GetTurnNumber(context.Kernel);

        if (IsAlreadySeen(fabric, conversationId, context.Function.Name, turnNumber))
        {
            await next(context);
            return;
        }
        MarkAsSeen(fabric, conversationId, context.Function.Name, turnNumber);

        // Step 3: Strategy resolution via registry + service provider.
        var descriptor = _registry.Find(context.Function.Name, context.Function.PluginName);
        if (descriptor is null)
        {
            _logger.LogWarning(
                "InferenceTriggerFilter: no descriptor for {FunctionName}/{PluginName}; skipping inference",
                context.Function.Name, context.Function.PluginName);
            await next(context);
            return;
        }

        if (descriptor.InferenceStrategy is null)
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "inference.skipped",
                tags: new ActivityTagsCollection
                {
                    { L2TelemetryKeys.FunctionName, context.Function.Name },
                    { L2TelemetryKeys.SkipReason, "no_strategy_registered" },
                }));
            _logger.LogWarning(
                "InferenceTriggerFilter: no strategy registered for {FunctionName}/{PluginName}; skipping inference",
                context.Function.Name, context.Function.PluginName);
            await next(context);
            return;
        }

        // Descriptor exists and has a strategy — emit inference.triggered before DI strategy resolution.
        Activity.Current?.AddEvent(new ActivityEvent(
            "inference.triggered",
            tags: new ActivityTagsCollection
            {
                { L2TelemetryKeys.FunctionName, context.Function.Name },
                { L2TelemetryKeys.PluginName, context.Function.PluginName ?? string.Empty },
                { L2TelemetryKeys.EntityType, descriptor.EntityType ?? string.Empty },
                { L2TelemetryKeys.StrategyType, descriptor.InferenceStrategy.FullName ?? string.Empty },
            }));

        ITaskInferenceStrategy? strategy;
        try
        {
            strategy = _serviceProvider.GetRequiredService(descriptor.InferenceStrategy) as ITaskInferenceStrategy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "InferenceTriggerFilter: could not resolve strategy {Type} for {FunctionName}; skipping inference",
                descriptor.InferenceStrategy.Name, context.Function.Name);
            await next(context);
            return;
        }

        if (strategy is null)
        {
            _logger.LogWarning(
                "InferenceTriggerFilter: strategy {Type} does not implement ITaskInferenceStrategy for {FunctionName}",
                descriptor.InferenceStrategy.Name, context.Function.Name);
            await next(context);
            return;
        }

        // Step 4: Run inference — result is already merged into fabric by TaskInferenceStep.
        // History is read from kernel.Data["ChatHistory"]; falls back to empty ChatHistory.
        // Per host convention: kernel.Data["ChatHistory"] is set by the host's agent runner before tool invocation.
        var history = context.Kernel.Data.TryGetValue("ChatHistory", out var histObj) && histObj is ChatHistory h
            ? h
            : new ChatHistory();

        try
        {
            await _runner.RunAsync(strategy, history, context.Function.Name, args, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-safe per PRD §3.2 — inference failure never breaks the tool call.
            _logger.LogWarning(ex,
                "InferenceTriggerFilter: inference failed for {FunctionName}; continuing tool call",
                context.Function.Name);
        }

        // Step 5: Tool call — always fires, regardless of inference outcome.
        await next(context);
    }

    // ── Idempotency helpers ───────────────────────────────────────────────────

    private static string GetConversationId(Kernel kernel, IContextFabric fabric)
    {
        if (kernel.Data.TryGetValue("ConversationId", out var cid) &&
            cid is string s && !string.IsNullOrEmpty(s))
            return s;
        // Stable per-fabric-instance fallback — conservative dedup within the fabric's lifetime.
        return RuntimeHelpers.GetHashCode(fabric).ToString(CultureInfo.InvariantCulture);
    }

    private static int GetTurnNumber(Kernel kernel)
    {
        if (!kernel.Data.TryGetValue("AffiantTurnNumber", out var tn)) return 0;
        return tn switch
        {
            int i => i,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => 0
        };
    }

    private static bool IsAlreadySeen(
        IContextFabric fabric, string conversationId, string functionName, int turnNumber)
    {
        var entity = fabric.GetByKey(IdempotencyEntityId);
        var key = $"{conversationId}|{functionName}|{turnNumber}";
        return entity is not null && entity.Fields.ContainsKey(key);
    }

    private static void MarkAsSeen(
        IContextFabric fabric, string conversationId, string functionName, int turnNumber)
    {
        var entity = fabric.GetByKey(IdempotencyEntityId);
        var key = $"{conversationId}|{functionName}|{turnNumber}";
        var fields = entity is not null
            ? new Dictionary<string, object>(entity.Fields)
            : new Dictionary<string, object>();
        fields[key] = true;
        fabric.Upsert(new EntityRef(
            EntityType: "__internal__",
            EntityId: IdempotencyEntityId,
            DisplayName: "Inference idempotency tracker",
            Fields: fields));
    }
}
