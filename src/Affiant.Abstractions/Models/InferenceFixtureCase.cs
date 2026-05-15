namespace Affiant.Abstractions.Models;

using Microsoft.SemanticKernel.ChatCompletion;

public sealed record InferenceFixtureCase(
    string Name,
    ChatHistory History,
    IReadOnlyDictionary<string, object?> Arguments,
    Func<Affidavit, bool> Assertion);
