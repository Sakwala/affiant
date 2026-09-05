namespace Affiant.Core.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-tool filter that records the arguments a model passed to a tool call as the values it
/// proposes, so the inference prompt and the projection can see them. Runs before the inference
/// trigger.
/// </summary>
/// <remarks>
/// <para>
/// <b>An argument is a value, not provenance</b> (PV-1). What the model wrote into a tool call is
/// the model's proposal; it says nothing about where the value came from, and this filter therefore
/// mints no tag for it. What is sworn about a field is whatever a deterministic interceptor or the
/// host's inference port says about it — and where neither speaks, the field is sworn
/// <see cref="ProvenanceSource.Empty"/> at confidence 0, which is the honest answer and which the
/// substance rule then acts on (GT-3).
/// </para>
/// <para>
/// It used to mint <see cref="ProvenanceSource.Conversation"/> at 0.9 for every argument — the
/// grade the ladder reserves for a value read out of the member's own turn. That put the model's
/// guess level with the member's words and, because the merge is confidence-first, ahead of a
/// literal from the turn reported at anything under 0.9. The card then showed the guess.
/// </para>
/// </remarks>
public sealed class ToolArgumentCaptureFilter : IToolInvocationFilter
{
    private readonly IContextFabric _fabric;
    private readonly IAffiantToolRegistry _registry;
    private readonly ILogger<ToolArgumentCaptureFilter> _logger;

    public ToolArgumentCaptureFilter(
        IContextFabric fabric,
        IAffiantToolRegistry registry,
        ILogger<ToolArgumentCaptureFilter> logger)
    {
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Whether an argument carries no value, in any of the shapes "no value" arrives in (AF-1).
    /// </summary>
    /// <remarks>
    /// A model's tool call reaches the framework through an adapter, and what an adapter hands over
    /// depends on how it parsed the JSON. Microsoft.Extensions.AI yields a C# <c>null</c>; another
    /// deserializer yields a <see cref="System.Text.Json.JsonElement"/> whose kind is <c>Null</c> or
    /// <c>Undefined</c>; a data-layer caller yields <see cref="DBNull"/>. All three say the same
    /// thing — the conversation said nothing about this field — and grading two of them
    /// <c>Conversation</c> at 0.9 would swear that it had.
    /// </remarks>
    private static bool CarriesNoValue(object? value) => value switch
    {
        null => true,
        DBNull => true,
        System.Text.Json.JsonElement element =>
            element.ValueKind is System.Text.Json.JsonValueKind.Null
                or System.Text.Json.JsonValueKind.Undefined,
        _ => false,
    };

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        // Only capture arguments for tools the framework tracks.
        var pluginName = string.IsNullOrEmpty(context.PluginName) ? null : context.PluginName;
        var descriptor = _registry.Find(context.FunctionName, pluginName);
        if (descriptor is not null && context.Arguments is not null)
        {
            var proposed = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (name, value) in context.Arguments)
            {
                // AF-1: only an argument that carries a value is tagged. An argument the model
                // passed as null is a field with nothing behind it, and the projection swears it
                // Empty at confidence 0 — which is what makes the aggregate 0 and the empty-field
                // count 1. Tagging it Conversation at 0.9 swore that the conversation had said
                // something, when what it had said was nothing.
                if (CarriesNoValue(value))
                {
                    _logger.LogDebug(
                        "ToolArgumentCaptureFilter: argument {Field} from {FunctionName} carries no " +
                        "value; it is sworn Empty rather than tagged",
                        name, context.FunctionName);
                    continue;
                }

                proposed[name] = value!;

                _logger.LogDebug(
                    "ToolArgumentCaptureFilter: captured argument {Field} from {FunctionName}",
                    name, context.FunctionName);
            }

            if (proposed.Count > 0 && descriptor.EntityType is { Length: > 0 } entityType)
            {
                _fabric.Upsert(new EntityRef(entityType, entityType, entityType, proposed));
            }
        }

        // Always call next — this filter is non-blocking in all paths.
        await next(context);
    }
}
