namespace Affiant.Extensions.AI.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Extensions;
using Affiant.Extensions.AI.Tests.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// End-to-end tests driving a real <see cref="FunctionInvokingChatClient"/> over a scripted
/// <see cref="IChatClient"/>, with tools wired through
/// <see cref="ChatOptionsExtensions.WithAffiant"/> — i.e. the whole seam, not the wrapper in
/// isolation. These are what prove design decision 1 (the wrapped-<see cref="AIFunction"/> seam)
/// actually carries Affiant's review-gate semantics rather than merely compiling.
///
/// Each of the review-gate powers the adapter contract requires gets one test here: provenance
/// capture, the docket round trip, result replacement, turn termination, and the deterministic
/// pre-tool short-circuit.
/// </summary>
public class AffiantDelegatingAIFunctionTests
{
    [Fact]
    public async Task ArgumentCaptureReachesContextFabric()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools);

        await RunOneTurnAsync(sp, client);

        var fabric = sp.GetRequiredService<ContextFabric>();
        var chain = fabric.GetFieldChain("name");

        Assert.NotNull(chain);
        Assert.Equal(ProvenanceSource.Conversation, chain!.Current.Source);
        Assert.Contains("CreateWidget", chain.Current.Evidence);
    }

    [Fact]
    public async Task WriteProposalEnvelopeRoutesThroughReviewGateOntoTheDocket()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools);

        await RunOneTurnAsync(sp, client);

        Assert.Single(docket.Filed);
        Assert.Equal("CreateWidget", docket.Filed[0].OperationType);
        // The fixture's standing-order policy auto-approves, so the entry resolves synchronously.
        Assert.Equal(ReviewStatus.Approved, docket.Filed[0].Status);
        // The real tool ran exactly once — the wrapper delegates to it, it does not shadow it.
        Assert.Equal(["gizmo"], tools.CreateCalls);
    }

    /// <summary>
    /// Result replacement at the seam: a completion-stage filter's substituted result is what the
    /// caller sees, not the tool's own return value. This is the power <c>ReviewGateFilter</c> uses
    /// to turn a write proposal into a turn-ending "queued for review" message.
    /// </summary>
    [Fact]
    public async Task FilterReplacedResultIsWhatTheCallerSees()
    {
        const string Replacement = "REPLACED-BY-A-FILTER";

        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("LookUpWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools, services =>
            services.AddScoped<IToolInvocationFilter>(_ => new ReplacingFilter(Replacement, terminate: false)));

        var response = await RunOneTurnAsync(sp, client);

        Assert.Equal(["gizmo"], tools.LookUpCalls);
        Assert.Equal(Replacement, SingleFunctionResult(response));
        Assert.DoesNotContain("widget:gizmo", SingleFunctionResult(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// Turn termination at the seam: a filter setting <c>Terminate</c> stops the chat loop, so the
    /// model is never asked for a follow-up completion. The spike proved the mechanism against raw
    /// Microsoft.Extensions.AI; this proves Affiant's pipeline actually drives it.
    /// </summary>
    [Fact]
    public async Task FilterTerminateStopsTheChatLoop()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("LookUpWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools, services =>
            services.AddScoped<IToolInvocationFilter>(_ => new ReplacingFilter("stopped", terminate: true)));

        await RunOneTurnAsync(sp, client);

        // One completion only: the tool-call turn. Without Terminate the loop asks again (see the
        // sibling test below, which is identical but for the flag).
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task WithoutTerminateTheChatLoopContinues()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("LookUpWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools, services =>
            services.AddScoped<IToolInvocationFilter>(_ => new ReplacingFilter("carry on", terminate: false)));

        await RunOneTurnAsync(sp, client);

        Assert.Equal(2, client.CallCount);
    }

    /// <summary>
    /// Deterministic short-circuit: a matching <see cref="IIntentInterceptor"/> answers before the
    /// tool body runs, so the real tool is never invoked at all and the caller sees the
    /// interceptor's answer.
    /// </summary>
    [Fact]
    public async Task MatchingIntentInterceptorShortCircuitsBeforeTheToolRuns()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("LookUpWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools, services =>
            services.AddSingleton<IIntentInterceptor>(new AlwaysMatchingInterceptor("SHORT-CIRCUITED")));

        var response = await RunOneTurnAsync(sp, client);

        Assert.Empty(tools.LookUpCalls);
        Assert.Equal("SHORT-CIRCUITED", SingleFunctionResult(response));
    }

    /// <summary>
    /// Bypass resistance (design decision 1's reason for wrapping the function rather than hooking
    /// the client): the pipeline runs even when the wrapper is invoked directly, outside any
    /// <see cref="FunctionInvokingChatClient"/> loop — the case where a
    /// <c>FunctionInvoker</c>-delegate design would silently do nothing.
    /// </summary>
    [Fact]
    public async Task DirectInvocationOutsideTheLoopStillRunsThePipeline()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("LookUpWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(client, docket, tools, services =>
            services.AddSingleton<IIntentInterceptor>(new AlwaysMatchingInterceptor("SHORT-CIRCUITED")));

        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        var function = wired.Tools!.OfType<AIFunction>().Single(f => f.Name == "LookUpWidget");
        var result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "gizmo" }) { Services = sp });

        Assert.Empty(tools.LookUpCalls);
        Assert.Equal("SHORT-CIRCUITED", result?.ToString());
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static async Task<ChatResponse> RunOneTurnAsync(IServiceProvider sp, IChatClient inner)
    {
        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);

        using var pipeline = new ChatClientBuilder(inner).UseFunctionInvocation().Build(sp);

        return await pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "please act on the widget")], wired);
    }

    private static string SingleFunctionResult(ChatResponse response) =>
        response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Single()
            .Result?.ToString() ?? string.Empty;

    /// <summary>Post-tool filter that swaps the result and optionally ends the turn.</summary>
    private sealed class ReplacingFilter(string replacement, bool terminate) : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            await next(context).ConfigureAwait(false);
            context.Result = replacement;
            if (terminate) context.Terminate = true;
        }
    }

    private sealed class AlwaysMatchingInterceptor(string answer) : IIntentInterceptor
    {
        public Task<bool> MatchesAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<object?> HandleAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(answer);
    }
}
