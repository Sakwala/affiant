namespace Affiant.Abstractions.Models;

/// <summary>
/// Backend-neutral chat message. Replaces Semantic Kernel's <c>ChatMessageContent</c> in the
/// framework's public contracts (<see cref="Affiant.Abstractions.Interfaces.IChatSessionStore"/>,
/// <see cref="InferenceCompletionRequest"/>, <see cref="InferenceFixtureCase"/>). Each backend
/// converts to and from its native message type at its own edge.
///
/// Inference consumes only <see cref="Role"/> and <see cref="Content"/>. The remaining optional
/// string fields exist to round-trip tool-call turns through <c>IChatSessionStore</c> without
/// data loss (they carry no backend type).
/// </summary>
public sealed record AffiantChatMessage(string Role, string Content)
{
    public string? AuthorName { get; init; }
    public string? ModelId { get; init; }
    public string? ToolCallId { get; init; }
    public string? FunctionName { get; init; }
    public string? ArgumentsJson { get; init; }
}
