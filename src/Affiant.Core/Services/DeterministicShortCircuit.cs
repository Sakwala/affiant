namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Microsoft.SemanticKernel;

public sealed class DeterministicShortCircuit(IEnumerable<IIntentInterceptor> interceptors)
    : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        IReadOnlyDictionary<string, object?> args = context.Arguments
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        foreach (var interceptor in interceptors)
        {
            if (await interceptor.MatchesAsync(args, context.CancellationToken).ConfigureAwait(false))
            {
                var result = await interceptor.HandleAsync(args, context.CancellationToken).ConfigureAwait(false);
                context.Result = new FunctionResult(context.Function, result);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
