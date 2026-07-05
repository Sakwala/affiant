namespace Affiant.SemanticKernel.Tests.Adapters;

using Affiant.Abstractions.Models;
using Affiant.SemanticKernel.Filters;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

/// <summary>
/// Round-trips an assistant tool-call turn and a tool-result turn through the SK edge conversions,
/// proving the R2 no-data-loss invariant holds for tool-call metadata (tool-call id, function name,
/// serialized arguments) and that the SK <see cref="ChatHistory"/> carries real
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> items — the shape
/// <c>SessionRehydrator</c> needs to reconstruct tool-call conversations on reconnect.
/// </summary>
public sealed class SkMessageConversionsTests
{
    [Fact]
    public void ToolCallTurn_AndToolResultTurn_RoundTripThroughSkConversions()
    {
        var original = new List<AffiantChatMessage>
        {
            new("assistant", string.Empty)
            {
                ToolCallId = "call_001",
                FunctionName = "SearchThing",
                ArgumentsJson = """{"id":"X-123"}""",
            },
            new("tool", "Found it.")
            {
                ToolCallId = "call_001",
                FunctionName = "SearchThing",
            },
        };

        var history = SkMessageConversions.ToChatHistory(original);

        var call = history[0].Items.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_001", call.Id);
        Assert.Equal("SearchThing", call.FunctionName);

        var toolResult = history[1].Items.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_001", toolResult.CallId);

        var roundTripped = SkMessageConversions.ToNeutral(history);

        Assert.Equal(2, roundTripped.Count);
        Assert.Equal("call_001", roundTripped[0].ToolCallId);
        Assert.Equal("SearchThing", roundTripped[0].FunctionName);
        Assert.Equal("""{"id":"X-123"}""", roundTripped[0].ArgumentsJson);

        Assert.Equal("call_001", roundTripped[1].ToolCallId);
        Assert.Equal("Found it.", roundTripped[1].Content);
    }
}
