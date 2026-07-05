namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Backend-neutral runner that owns the canonical filter order (framework spec §3.12.4). Each
/// interception backend translates its native invocation context into a
/// <see cref="ToolInvocationRequest"/>, supplies a filter selector and a terminal delegate
/// (which runs the actual tool through the backend), and reads the returned
/// <see cref="ToolInvocationContext"/> back into its native context.
///
/// Filters execute as an onion in registration order: <c>filters[0]</c> is outermost. Code before
/// <c>await next(...)</c> is pre-invocation; code after is post-invocation.
///
/// Scope flow: the pipeline resolves filters (and, transitively, the conversation-scoped
/// <c>IContextFabric</c>) from the <em>caller's ambient service scope</em> when the backend supplies
/// one via <paramref name="ambientProvider"/> — this is what keeps every filter in one tool call, and
/// (for backends that fire the pipeline more than once per call, e.g. Semantic Kernel's
/// invocation/auto-invocation split) every stage of that call, bound to the same fabric instance. When
/// no ambient provider is supplied the runner owns a fresh scope per invocation, created from the
/// injected <see cref="IServiceScopeFactory"/> and disposed when the call returns.
/// </summary>
public sealed class ToolInvocationPipeline
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ToolInvocationPipeline(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<ToolInvocationContext> RunAsync(
        ToolInvocationRequest request,
        Func<IReadOnlyList<IToolInvocationFilter>, IReadOnlyList<IToolInvocationFilter>> selectFilters,
        Func<ToolInvocationContext, Task> terminal,
        IServiceProvider? ambientProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectFilters);
        ArgumentNullException.ThrowIfNull(terminal);

        // Use the caller's ambient scope when provided so filters + the conversation-scoped fabric
        // resolve in the caller's turn scope; otherwise own a fresh scope for this one invocation.
        IServiceScope? ownedScope = ambientProvider is null ? _scopeFactory.CreateScope() : null;
        var provider = ambientProvider ?? ownedScope!.ServiceProvider;

        try
        {
            var filters = selectFilters(provider.GetServices<IToolInvocationFilter>().ToList());

            var context = new ToolInvocationContext
            {
                FunctionName = request.FunctionName,
                PluginName = request.PluginName,
                Arguments = request.Arguments,
                Result = request.InitialResult,
                Terminate = request.InitialTerminate,
                Services = provider,
                ConversationId = request.ConversationId,
                TurnNumber = request.TurnNumber,
                History = request.History,
            };

            var pipeline = terminal;
            for (var i = filters.Count - 1; i >= 0; i--)
            {
                var filter = filters[i];
                var next = pipeline;
                pipeline = ctx => filter.OnToolInvocationAsync(ctx, next, cancellationToken);
            }

            await pipeline(context).ConfigureAwait(false);
            return context;
        }
        finally
        {
            ownedScope?.Dispose();
        }
    }
}

/// <summary>
/// Backend-supplied seed for a single tool invocation. The backend fills identity, arguments, and
/// ambient turn context from its native invocation context.
/// </summary>
public sealed record ToolInvocationRequest(
    string FunctionName,
    string PluginName,
    IDictionary<string, object?> Arguments)
{
    public object? InitialResult { get; init; }
    public bool InitialTerminate { get; init; }
    public string? ConversationId { get; init; }
    public int TurnNumber { get; init; }
    public IReadOnlyList<AffiantChatMessage> History { get; init; } = [];
}
