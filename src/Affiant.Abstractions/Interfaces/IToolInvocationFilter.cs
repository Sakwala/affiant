namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// A backend-neutral tool-interception filter. Filters wrap <paramref name="next"/> in an onion:
/// code before <c>await next(...)</c> is pre-invocation, code after is post-invocation. A filter
/// may replace <see cref="ToolInvocationContext.Result"/> after <c>next</c> completes and may set
/// <see cref="ToolInvocationContext.Terminate"/>. Order is owned by the pipeline runner.
/// </summary>
public interface IToolInvocationFilter
{
    Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default);
}
