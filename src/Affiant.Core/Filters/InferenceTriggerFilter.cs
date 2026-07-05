namespace Affiant.Core.Filters;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-tool filter that fires structured-output inference before a registered write-intent tool
/// executes. For each <see cref="IInferenceTrigger"/> that returns true, resolves the tool's
/// <see cref="ITaskInferenceStrategy"/> and invokes <see cref="TaskInferenceRunner"/>.
///
/// Algorithm per L2 PRD §3.2:
///   1. Trigger evaluation — short-circuits on first true.
///   2. Idempotency check — once per (ConversationId, FunctionName, TurnNumber).
///      Bookkeeping anchored on IContextFabric via reserved entity key "inference_idempotency".
///   3. Strategy resolution — from IAffiantToolRegistry + the per-invocation service scope.
///   4. Run inference — fail-safe: any non-cancellation exception logs a warning + continues.
///   5. Tool call — next(context) always fires in every path.
/// </summary>
public sealed class InferenceTriggerFilter : IToolInvocationFilter
{
    // Reserved key under which the idempotency tracker entity is stored in IContextFabric.
    private const string IdempotencyEntityId = "inference_idempotency";

    private readonly IEnumerable<IInferenceTrigger> _triggers;
    private readonly TaskInferenceRunner _runner;
    private readonly IContextFabric _fabric;
    private readonly IAffiantToolRegistry _registry;
    private readonly ILogger<InferenceTriggerFilter> _logger;

    public InferenceTriggerFilter(
        IEnumerable<IInferenceTrigger> triggers,
        TaskInferenceRunner runner,
        IContextFabric fabric,
        IAffiantToolRegistry registry,
        ILogger<InferenceTriggerFilter> logger)
    {
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        var pluginName = string.IsNullOrEmpty(context.PluginName) ? null : context.PluginName;

        // Step 1: Trigger evaluation — short-circuit on first true trigger.
        IReadOnlyDictionary<string, object?> args = context.Arguments is not null
            ? context.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value)
            : new Dictionary<string, object?>(0);

        var triggerCtx = new InferenceTriggerContext(
            FunctionName: context.FunctionName,
            PluginName: context.PluginName,
            Arguments: args,
            Fabric: _fabric,
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
            var skipDescriptor = _registry.Find(context.FunctionName, pluginName);
            if (skipDescriptor is not null)
            {
                Activity.Current?.AddEvent(new ActivityEvent(
                    "inference.skipped",
                    tags: new ActivityTagsCollection
                    {
                        { L2TelemetryKeys.FunctionName, context.FunctionName },
                        { L2TelemetryKeys.SkipReason, "not_a_write_tool" },
                    }));
            }
            await next(context);
            return;
        }

        // Step 2: Idempotency check — (ConversationId, FunctionName, TurnNumber).
        var conversationId = GetConversationId(context, _fabric);
        var turnNumber = context.TurnNumber;

        if (IsAlreadySeen(_fabric, conversationId, context.FunctionName, turnNumber))
        {
            await next(context);
            return;
        }
        MarkAsSeen(_fabric, conversationId, context.FunctionName, turnNumber);

        // Step 3: Strategy resolution via registry + per-invocation scope.
        var descriptor = _registry.Find(context.FunctionName, pluginName);
        if (descriptor is null)
        {
            _logger.LogWarning(
                "InferenceTriggerFilter: no descriptor for {FunctionName}/{PluginName}; skipping inference",
                context.FunctionName, context.PluginName);
            await next(context);
            return;
        }

        if (descriptor.InferenceStrategy is null)
        {
            Activity.Current?.AddEvent(new ActivityEvent(
                "inference.skipped",
                tags: new ActivityTagsCollection
                {
                    { L2TelemetryKeys.FunctionName, context.FunctionName },
                    { L2TelemetryKeys.SkipReason, "no_strategy_registered" },
                }));
            _logger.LogWarning(
                "InferenceTriggerFilter: no strategy registered for {FunctionName}/{PluginName}; skipping inference",
                context.FunctionName, context.PluginName);
            await next(context);
            return;
        }

        // Descriptor exists and has a strategy — emit inference.triggered before strategy resolution.
        Activity.Current?.AddEvent(new ActivityEvent(
            "inference.triggered",
            tags: new ActivityTagsCollection
            {
                { L2TelemetryKeys.FunctionName, context.FunctionName },
                { L2TelemetryKeys.PluginName, context.PluginName },
                { L2TelemetryKeys.EntityType, descriptor.EntityType ?? string.Empty },
                { L2TelemetryKeys.StrategyType, descriptor.InferenceStrategy.FullName ?? string.Empty },
            }));

        ITaskInferenceStrategy? strategy;
        try
        {
            strategy = context.Services.GetRequiredService(descriptor.InferenceStrategy) as ITaskInferenceStrategy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "InferenceTriggerFilter: could not resolve strategy {Type} for {FunctionName}; skipping inference",
                descriptor.InferenceStrategy.Name, context.FunctionName);
            await next(context);
            return;
        }

        if (strategy is null)
        {
            _logger.LogWarning(
                "InferenceTriggerFilter: strategy {Type} does not implement ITaskInferenceStrategy for {FunctionName}",
                descriptor.InferenceStrategy.Name, context.FunctionName);
            await next(context);
            return;
        }

        // Step 4: Run inference — result is already merged into the fabric by TaskInferenceStep.
        try
        {
            await _runner.RunAsync(strategy, context.History, context.FunctionName, args, cancellationToken)
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
                context.FunctionName);
        }

        // Step 5: Tool call — always fires, regardless of inference outcome.
        await next(context);
    }

    // ── Idempotency helpers ───────────────────────────────────────────────────

    private static string GetConversationId(ToolInvocationContext context, IContextFabric fabric)
    {
        if (!string.IsNullOrEmpty(context.ConversationId))
            return context.ConversationId;
        // Stable per-fabric-instance fallback — conservative dedup within the fabric's lifetime.
        return RuntimeHelpers.GetHashCode(fabric).ToString(CultureInfo.InvariantCulture);
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
