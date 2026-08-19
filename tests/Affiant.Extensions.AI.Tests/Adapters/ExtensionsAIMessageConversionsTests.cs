namespace Affiant.Extensions.AI.Tests.Adapters;

using Affiant.Abstractions.Models;
using Affiant.Extensions.AI.Adapters;
using Microsoft.Extensions.AI;
using Xunit;

/// <summary>
/// Round-trips an assistant tool-call turn and a tool-result turn through this package's edge
/// conversions, proving the R2 no-data-loss invariant holds for tool-call metadata (tool-call id,
/// function name, serialized arguments) and that the produced <see cref="ChatMessage"/> list carries
/// real <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> items rather than
/// flattened text.
///
/// <para>
/// The mirror of <c>tests/Affiant.AgentFramework.Tests/Adapters/MafMessageConversionsTests.cs</c>,
/// against the copy this package took of that file (design brief decision 3). The conversions matter
/// at two points here, not one: <c>AffiantDelegatingAIFunction</c> uses <c>ToNeutral</c> to hand the
/// loop's history to the neutral pipeline, and <c>ExtensionsAIInferenceCompletionPort</c> uses
/// <c>ToChatMessages</c> to rebuild that history for the tool-free inference call — so a lossy
/// conversion would quietly degrade what the extraction model is shown.
/// </para>
/// </summary>
public sealed class ExtensionsAIMessageConversionsTests
{
    [Fact]
    public void ToolCallTurn_AndToolResultTurn_RoundTripThroughExtensionsAIConversions()
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

        var messages = ExtensionsAIMessageConversions.ToChatMessages(original);

        var call = messages[0].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_001", call.CallId);
        Assert.Equal("SearchThing", call.Name);

        var toolResult = messages[1].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("call_001", toolResult.CallId);

        var roundTripped = ExtensionsAIMessageConversions.ToNeutral(messages);

        Assert.Equal(2, roundTripped.Count);
        Assert.Equal("call_001", roundTripped[0].ToolCallId);
        Assert.Equal("SearchThing", roundTripped[0].FunctionName);
        Assert.Equal("""{"id":"X-123"}""", roundTripped[0].ArgumentsJson);

        Assert.Equal("call_001", roundTripped[1].ToolCallId);
        Assert.Equal("Found it.", roundTripped[1].Content);
    }

    /// <summary>
    /// Plain conversational turns survive with role, text and author name intact and pick up no
    /// spurious tool-call metadata — the case that runs on every turn of every conversation, and the
    /// one a tool-call-shaped conversion is most likely to over-fit against.
    /// </summary>
    [Fact]
    public void PlainTextTurns_RoundTripWithoutAcquiringToolCallMetadata()
    {
        var original = new List<AffiantChatMessage>
        {
            new("user", "create a widget called gizmo") { AuthorName = "seevali" },
            new("assistant", "Done."),
        };

        var messages = ExtensionsAIMessageConversions.ToChatMessages(original);

        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("seevali", messages[0].AuthorName);
        Assert.Empty(messages[0].Contents.OfType<FunctionCallContent>());

        var roundTripped = ExtensionsAIMessageConversions.ToNeutral(messages);

        Assert.Equal("create a widget called gizmo", roundTripped[0].Content);
        Assert.Equal("seevali", roundTripped[0].AuthorName);
        Assert.Null(roundTripped[0].ToolCallId);
        Assert.Null(roundTripped[0].FunctionName);
        Assert.Null(roundTripped[0].ArgumentsJson);

        Assert.Equal("assistant", roundTripped[1].Role);
        Assert.Equal("Done.", roundTripped[1].Content);
    }
}
