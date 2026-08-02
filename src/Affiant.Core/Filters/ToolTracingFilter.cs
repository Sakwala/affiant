using System.Diagnostics;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;

namespace Affiant.Core.Filters;

/// <summary>
/// Framework filter that creates a single <c>execute_tool</c> span for every tool invocation.
/// Registered by <c>AddAffiantCore()</c> — hosts do not need to register it manually.
///
/// The span carries <c>gen_ai.tool.name</c> (the function name) and <c>tool_status</c>
/// (<c>"ok"</c>, <c>"empty"</c>, or <c>"error"</c>). It remains active throughout the full inner
/// filter chain (host <c>ContextExtractor</c> subclasses, <c>TaskInferenceMergeFilter</c>, and the
/// tool), so all inner events and attributes are automatically attached to this span.
///
/// Placed inside <c>ToolErrorFilter</c> in the pipeline. When a tool throws, this filter
/// records <c>ActivityStatusCode.Error</c> and <c>tool_status="error"</c> on the span before
/// disposal. The <c>affiant.tool_error</c> event (emitted by the outer <c>ToolErrorFilter</c>)
/// lands on the nearest parent <c>Affiant.Framework</c> span via <c>AffiantTelemetry.FindAffiantActivity</c>
/// walk-up, because the <c>execute_tool</c> span is disposed before <c>ToolErrorFilter</c>'s
/// catch block runs.
///
/// <para>
/// <b>Telemetry honesty for RETURNED errors (P1d, area-3 V6):</b> a tool can also fail by
/// <i>returning</i> a <see cref="ToolError"/> envelope rather than throwing (e.g. a host's redirect
/// protocol, or — on MAF, where this filter wraps <c>ReviewGateFilter</c> in the same onion —
/// <c>ReviewGateFilter</c>'s own filing-failure rewrite). Without this check such a result looked
/// identical to a successful tool call: <c>tool_status="ok"</c>, no <c>affiant.tool_error</c> event,
/// invisible to operators. This filter now inspects the post-invocation result for the
/// <see cref="ToolEnvelope"/> <c>$type: "error"</c> discriminator and, when found, tags
/// <c>tool_status="error"</c> and emits the same <c>affiant.tool_error</c> event shape
/// <c>ToolErrorFilter</c> emits for thrown errors — one operator-visible vocabulary for both —
/// distinguishing the two via <c>exception.type</c> (<see cref="ReturnedToolErrorExceptionType"/>
/// for a returned envelope, the real CLR exception type name for a thrown one).
/// </para>
/// </summary>
public sealed class ToolTracingFilter : IToolInvocationFilter
{
    /// <summary>
    /// Sentinel <c>exception.type</c> tag value for a <c>affiant.tool_error</c> event emitted from a
    /// RETURNED <see cref="ToolError"/> (no CLR exception exists to name) — distinguishes these events
    /// from <c>ToolErrorFilter</c>'s thrown-exception events in the same <c>affiant.tool_error</c>
    /// vocabulary without inventing a fake exception type name.
    /// </summary>
    public const string ReturnedToolErrorExceptionType = "ReturnedToolError";

    private static readonly JsonSerializerOptions ToolEnvelopeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        var activity = AffiantTelemetry.AffiantActivitySource.StartActivity(
            "execute_tool", ActivityKind.Internal);
        activity?.SetTag("gen_ai.tool.name", context.FunctionName);

        try
        {
            await next(context);

            if (activity is not null)
            {
                var resultText = context.Result as string ?? context.Result?.ToString();
                if (TryParseToolError(resultText, out var toolError))
                {
                    activity.SetTag("tool_status", "error");
                    activity.AddEvent(new ActivityEvent("affiant.tool_error",
                        tags: new ActivityTagsCollection
                        {
                            { "tool_error.code", toolError!.Code },
                            { "tool_error.retryable", toolError.Retryable },
                            { "exception.type", ReturnedToolErrorExceptionType }
                        }));
                }
                else
                {
                    activity.SetTag("tool_status", string.IsNullOrEmpty(resultText) ? "empty" : "ok");
                }
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("tool_status", "error");
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    /// <summary>
    /// Attempts to parse <paramref name="resultText"/> as a <see cref="ToolEnvelope"/> and extract a
    /// <see cref="ToolError"/> variant. Returns <c>false</c> — not a <see cref="ToolError"/> — for
    /// null/empty text, non-JSON text (most tool results are plain markdown/summary strings, not
    /// JSON), or JSON missing the polymorphic <c>$type</c> discriminator, mirroring the same
    /// tolerant-parse pattern <c>ReviewGateFilter</c> uses to detect <see cref="WriteProposal"/>.
    /// </summary>
    private static bool TryParseToolError(string? resultText, out ToolError? toolError)
    {
        toolError = null;
        if (string.IsNullOrEmpty(resultText)) return false;

        try
        {
            toolError = JsonSerializer.Deserialize<ToolEnvelope>(resultText, ToolEnvelopeJsonOptions) as ToolError;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }

        return toolError is not null;
    }
}
