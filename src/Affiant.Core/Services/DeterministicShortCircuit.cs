namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

public sealed class DeterministicShortCircuit(IEnumerable<IIntentInterceptor> interceptors)
    : IToolInvocationFilter
{
    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, object?> args = context.Arguments
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        foreach (var interceptor in interceptors)
        {
            if (await interceptor.MatchesAsync(args, cancellationToken).ConfigureAwait(false))
            {
                context.Result = await interceptor.HandleAsync(args, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
