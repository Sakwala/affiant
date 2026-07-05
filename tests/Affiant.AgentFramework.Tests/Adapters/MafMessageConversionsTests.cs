namespace Affiant.AgentFramework.Tests.Adapters;

using Affiant.Abstractions.Models;
using Affiant.AgentFramework.Adapters;
using Microsoft.Extensions.AI;
using Xunit;

/// <summary>
/// Round-trips an assistant tool-call turn and a tool-result turn through the MAF edge conversions,
/// proving the R2 no-data-loss invariant holds for tool-call metadata (tool-call id, function name,
/// serialized arguments) and that the MAF <see cref="ChatMessage"/> list carries real
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> items.
/// </summary>
public sealed class MafMessageConversionsTests
{
    [Fact]
    public void ToolCallTurn_AndToolResultTurn_RoundTripThroughMafConversions()
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
            },
        };

        var messages = MafMessageConversions.ToChatMessages(original);

        var call = messages[0].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_001", call.CallId);
        Assert.Equal("SearchThing", call.Name);

        var toolResult = messages[1].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_001", toolResult.CallId);

        var roundTripped = MafMessageConversions.ToNeutral(messages);

        Assert.Equal(2, roundTripped.Count);
        Assert.Equal("call_001", roundTripped[0].ToolCallId);
        Assert.Equal("SearchThing", roundTripped[0].FunctionName);
        Assert.Equal("""{"id":"X-123"}""", roundTripped[0].ArgumentsJson);

        Assert.Equal("call_001", roundTripped[1].ToolCallId);
        Assert.Equal("Found it.", roundTripped[1].Content);
    }
}
