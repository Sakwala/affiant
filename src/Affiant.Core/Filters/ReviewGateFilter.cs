namespace Affiant.Core.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Completion-stage filter that routes WriteProposal results through ReviewGate.
///
/// Fires after each auto-invoked tool. If the tool result deserializes as a WriteProposal, the
/// proposal is routed through ReviewGate.FileReviewAsync using the pipeline's per-invocation
/// scope (<see cref="ToolInvocationContext.Services"/>). Silently skips when IReviewContextProvider
/// or ReviewGate are not registered in the DI container, so the filter is safe to register globally
/// even in hosts that do not use the full review infrastructure.
/// </summary>
public sealed class ReviewGateFilter(ILogger<ReviewGateFilter> logger) : IToolInvocationFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task OnToolInvocationAsync(
        ToolInvocationContext context,
        Func<ToolInvocationContext, Task> next,
        CancellationToken cancellationToken = default)
    {
        await next(context);

        var resultString = context.Result as string ?? context.Result?.ToString();
        if (string.IsNullOrEmpty(resultString))
            return;

        WriteProposal? proposal;
        try
        {
            var envelope = JsonSerializer.Deserialize<ToolEnvelope>(resultString, JsonOptions);
            proposal = envelope as WriteProposal;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // STJ throws JsonException for malformed JSON and NotSupportedException when a
            // polymorphic type (ToolEnvelope) lacks the required $type discriminator.
            // Both mean the result is not a WriteProposal — skip silently.
            return;
        }

        if (proposal is null)
            return;

        var contextProvider = context.Services.GetService<IReviewContextProvider>();
        if (contextProvider is null)
        {
            logger.LogDebug(
                "ReviewGateFilter: IReviewContextProvider not registered; skipping review for {ToolName}",
                proposal.ToolName);
            return;
        }

        var reviewContext = contextProvider.BuildReviewContext(proposal);
        if (reviewContext is null)
        {
            logger.LogDebug(
                "ReviewGateFilter: no ambient review context available; skipping review for {ToolName}",
                proposal.ToolName);
            return;
        }

        var gate = context.Services.GetService<ReviewGate>();
        if (gate is null)
        {
            logger.LogDebug(
                "ReviewGateFilter: ReviewGate not registered; skipping review for {ToolName}",
                proposal.ToolName);
            return;
        }

        try
        {
            var outcome = await gate.FileReviewAsync(proposal, reviewContext);
            logger.LogInformation(
                "ReviewGateFilter: filed review for {ToolName}: {OutcomeType}",
                proposal.ToolName, outcome.GetType().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "ReviewGateFilter: ReviewGate.FileReviewAsync failed for {ToolName}",
                proposal.ToolName);
        }
    }
}
