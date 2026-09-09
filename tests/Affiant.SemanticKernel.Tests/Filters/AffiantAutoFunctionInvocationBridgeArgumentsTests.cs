namespace Affiant.SemanticKernel.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.SemanticKernel.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// affiant#114: what the SK completion-stage bridge hands the neutral pipeline as the call's
/// arguments. <c>ReviewGateFilter</c> runs at this stage and attaches
/// <see cref="WriteProposal.Arguments"/> from <c>ToolInvocationContext.Arguments</c>, and those
/// arguments are part of the entry-id material a filed proposal derives from (GT-4). Until this
/// fix the bridge passed an empty dictionary, so on SK — and on SK alone — a filed proposal never
/// carried them.
/// </summary>
public class AffiantAutoFunctionInvocationBridgeArgumentsTests
{
    [Fact]
    public async Task Bridge_PassesTheCallsArguments_ToTheCompletionStage()
    {
        var recorder = new ArgumentRecordingFilter();
        var (pipeline, services) = BuildPipeline(recorder);
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        using var scope = services.CreateScope();
        var context = BuildAutoInvocationContext(
            scope.ServiceProvider, "DoWrite",
            new KernelArguments { ["title"] = "Q3 report", ["amount"] = 42 });

        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        Assert.NotNull(recorder.Seen);
        Assert.Equal("Q3 report", recorder.Seen!["title"]);
        Assert.Equal(42, recorder.Seen["amount"]);
    }

    [Fact]
    public async Task Bridge_WhenSkHoldsNoKernelArguments_PassesAnEmptySet_AndDoesNotThrow()
    {
        // SK's AutoFunctionInvocationContext.Arguments throws InvalidOperationException when the
        // loop's arguments are not a KernelArguments — a context built without any is exactly that
        // case. The bridge must still run the completion stage.
        var recorder = new ArgumentRecordingFilter();
        var (pipeline, services) = BuildPipeline(recorder);
        var bridge = new AffiantAutoFunctionInvocationBridge(pipeline);

        using var scope = services.CreateScope();
        var context = BuildAutoInvocationContext(scope.ServiceProvider, "DoWrite");

        await bridge.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);

        Assert.NotNull(recorder.Seen);
        Assert.Empty(recorder.Seen!);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (ToolInvocationPipeline Pipeline, ServiceProvider Services) BuildPipeline(
        IToolInvocationFilter recorder)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        var sp = services.BuildServiceProvider();
        return (new ToolInvocationPipeline(sp.GetRequiredService<IServiceScopeFactory>()), sp);
    }

    private static AutoFunctionInvocationContext BuildAutoInvocationContext(
        IServiceProvider services, string functionName, KernelArguments? arguments = null)
    {
        var kernel = new Kernel(services);
        var function = KernelFunctionFactory.CreateFromMethod(() => "tool-result", functionName);
        var initialResult = new FunctionResult(function, "tool-result");
        var chatMessage = new ChatMessageContent(AuthorRole.Assistant, $"calling {functionName}");

        // Arguments is init-only on SK's context, so the two cases are two constructions.
        return arguments is null
            ? new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage)
            : new AutoFunctionInvocationContext(kernel, function, initialResult, new ChatHistory(), chatMessage)
            {
                Arguments = arguments,
            };
    }

    /// <summary>
    /// Stands where <c>ReviewGateFilter</c> stands — a completion-stage filter — and records the
    /// arguments the neutral context carried when the stage ran.
    /// </summary>
    private sealed class ArgumentRecordingFilter : IToolInvocationFilter, ICompletionStageFilter
    {
        public IDictionary<string, object?>? Seen { get; private set; }

        public Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            Seen = context.Arguments;
            return next(context);
        }
    }
}
