namespace Affiant.Extensions.AI.Tests.Filters;

using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Extensions.AI.Extensions;
using Affiant.Extensions.AI.Tests.Utilities;
using Microsoft.Extensions.AI;
using Xunit;

/// <summary>
/// The same review-gate powers as <see cref="ReviewGateSemanticsAtTheSeamTests"/>, on the
/// <em>streaming</em> half of <see cref="IChatClient"/>.
///
/// <para>
/// <b>Why this is a separate file and not an assumed equivalence.</b> Acceptance criterion 4 of the
/// design brief (<c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>) is
/// written without reference to a response mode, and every test proving it ran through
/// <see cref="IChatClient.GetResponseAsync"/> — while real hosts stream. The two paths are separate
/// implementations inside <see cref="FunctionInvokingChatClient"/>: the streaming one reconstructs
/// function calls out of coalesced <see cref="ChatResponseUpdate"/>s and emits each
/// <see cref="FunctionResultContent"/> onto the stream itself. Terminating a turn mid-stream is
/// therefore a distinct claim from terminating a buffered one, and "it is the same wrapper" is an
/// argument, not evidence. This file is the evidence.
/// </para>
/// </summary>
public class StreamingSeamTests
{
    /// <summary>
    /// The message <c>Affiant.Core.Filters.ReviewGateFilter</c> substitutes for the tool's own result
    /// when a filing needs a human. Duplicated from <see cref="ReviewGateSemanticsAtTheSeamTests"/>
    /// rather than shared, so that each file states independently what the model is supposed to see.
    /// </summary>
    private const string TurnEndingMessage =
        "This action has been filed for review — check the Evidence Card to approve, reject, or amend it.";

    /// <summary>
    /// <b>Block and replace-result, streamed.</b> A write whose policy demands a reviewer ends the turn
    /// on the streaming path too: the <see cref="FunctionResultContent"/> that reaches the stream is
    /// the gate's message rather than the tool's <c>WriteProposal</c> JSON, the loop never goes back to
    /// the model, and the docket entry and Evidence Card the reviewer will act on both exist.
    /// </summary>
    [Fact]
    public async Task Streaming_ReviewerConfirmationRequired_EndsTheTurnAndStreamsTheGatesMessage()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var transport = new RecordingStreamingTransport();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools,
            approvalPolicy: new ReviewerConfirmationPolicy(),
            transport: transport,
            inferenceChatClient: new StubInferenceChatClient());

        var updates = await StreamOneTurnAsync(sp, client);

        Assert.Equal(["gizmo"], tools.CreateCalls);

        // The loop-continuation witness: 1 means the turn ended after the tool ran.
        Assert.Equal(1, client.CallCount);

        var result = updates
            .SelectMany(u => u.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        Assert.Equal(TurnEndingMessage, result.Result?.ToString());
        Assert.DoesNotContain("WriteProposal", result.Result?.ToString() ?? string.Empty, StringComparison.Ordinal);

        var filed = Assert.Single(docket.Filed);
        Assert.Equal(ReviewStatus.Pending, filed.Status);
        Assert.Contains(transport.Broadcasts, b => b.Event == TransportEvent.EvidenceCardRequest);
    }

    /// <summary>
    /// The paired control. With the fixture's standing-order policy nothing needs a human, so the gate
    /// deliberately does not terminate and the streaming loop goes back to the model — proving the
    /// test above pins the gate's verdict rather than some incidental property of the streaming path.
    /// </summary>
    [Fact]
    public async Task Streaming_StandingOrderAutoApproval_LetsTheTurnContinue()
    {
        var tools = new WidgetTools();
        var docket = new FakeDocketStore();
        var client = new ScriptedChatClient("CreateWidget", new Dictionary<string, object?> { ["name"] = "gizmo" });
        using var sp = AffiantTestHost.Build(
            client, docket, tools, inferenceChatClient: new StubInferenceChatClient());

        await StreamOneTurnAsync(sp, client);

        Assert.Equal(2, client.CallCount);
        Assert.Equal(ReviewStatus.Approved, Assert.Single(docket.Filed).Status);
    }

    private static async Task<List<ChatResponseUpdate>> StreamOneTurnAsync(IServiceProvider sp, IChatClient inner)
    {
        var catalog = AffiantToolCatalog.FromType<WidgetTools>();
        var wired = new ChatOptions { Tools = [.. catalog.Functions] }.WithAffiant(sp, catalog);
        wired.ConversationId = "conversation-streaming";

        using var pipeline = new ChatClientBuilder(inner).UseFunctionInvocation().Build(sp);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in pipeline.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "please create the widget")], wired))
        {
            updates.Add(update);
        }

        return updates;
    }
}
