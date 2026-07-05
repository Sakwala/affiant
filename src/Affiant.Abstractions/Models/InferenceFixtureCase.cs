namespace Affiant.Abstractions.Models;

public sealed record InferenceFixtureCase(
    string Name,
    IReadOnlyList<AffiantChatMessage> History,
    IReadOnlyDictionary<string, object?> Arguments,
    Func<Affidavit, bool> Assertion);
