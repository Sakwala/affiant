namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// Observability hook for tool invocation events. Implementations may feed
/// compliance logs, analytics, or tracing systems without affecting tool execution.
/// </summary>
public interface IToolInvocationCapture
{
    Task OnToolInvokedAsync(string functionName, Dictionary<string, object?> arguments, CancellationToken ct);
    Task OnToolSucceededAsync(string functionName, ToolEnvelope result, long durationMs, CancellationToken ct);
    Task OnToolFailedAsync(string functionName, Exception exception, long durationMs, CancellationToken ct);
}
