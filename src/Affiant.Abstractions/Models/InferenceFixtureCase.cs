namespace Affiant.Abstractions.Models;

/// <summary>
/// One case in a compliance fixture: the conversation to replay, the arguments the write tool was
/// called with, and the assertion the resulting <see cref="Affidavit"/> must satisfy.
/// </summary>
/// <param name="EntityId">
/// The entity this case's operation targets, for a write tool whose descriptor declares an update
/// operation. An update-shaped Affidavit names the entity it updates, so the harness cannot project
/// one without it; a create-shaped case leaves this null, which is the default and what every
/// existing fixture keeps.
/// </param>
public sealed record InferenceFixtureCase(
    string Name,
    IReadOnlyList<AffiantChatMessage> History,
    IReadOnlyDictionary<string, object?> Arguments,
    Func<Affidavit, bool> Assertion,
    string? EntityId = null);
