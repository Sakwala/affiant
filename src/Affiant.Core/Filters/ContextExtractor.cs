namespace Affiant.Core.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// Abstract base class for context extractors. Each host application subclasses this
/// once per tool (or per tool group), overriding ExtractAsync to implement
/// domain-specific extraction logic from ReadResult.Entities.
///
/// The base class handles:
/// - <see cref="IToolInvocationFilter"/> wiring (runs after the tool, before the caller resumes)
/// - JSON deserialization of the ToolEnvelope
/// - Tool-name matching via abstract MatchesTool
/// - EmitEntity helper that upserts into ContextFabric with structured logging
/// </summary>
public abstract class ContextExtractor : IToolInvocationFilter
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected readonly ContextFabric ContextFabric;
    protected readonly ILogger Logger;

    protected ContextExtractor(ContextFabric contextFabric, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contextFabric);
        ArgumentNullException.ThrowIfNull(logger);
        ContextFabric = contextFabric;
        Logger = logger;
    }

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        await next(context);

        if (!MatchesTool(context.FunctionName)) return;

        var resultText = context.Result as string ?? context.Result?.ToString();
        if (string.IsNullOrWhiteSpace(resultText)) return;

        ReadResult? readResult = null;
        if (resultText.Contains("\"$type\""))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<ToolEnvelope>(resultText, EnvelopeOptions);
                readResult = envelope as ReadResult;
            }
            catch (JsonException) { }
        }

        if (readResult is null || readResult.Entities.Length == 0) return;

        await ExtractAsync(readResult, context);
    }

    /// <summary>
    /// Returns true if this extractor handles the given tool name.
    /// Use StringComparison.OrdinalIgnoreCase to match backend tool registration.
    /// </summary>
    protected abstract bool MatchesTool(string toolName);

    /// <summary>
    /// Override to extract domain-specific EntityRef instances from the ReadResult.
    /// Call EmitEntity for each extracted entity.
    /// </summary>
    protected abstract Task ExtractAsync(ReadResult result, ToolInvocationContext context);

    /// <summary>Upserts an EntityRef into the ContextFabric with structured logging.</summary>
    protected void EmitEntity(EntityRef entityRef)
    {
        ArgumentNullException.ThrowIfNull(entityRef);
        ContextFabric.Upsert(entityRef);
        Logger.LogDebug(
            "Extracted entity {EntityId} of type {EntityType} with {FieldCount} fields",
            entityRef.EntityId,
            entityRef.EntityType,
            entityRef.Fields.Count);
    }
}
