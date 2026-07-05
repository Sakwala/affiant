namespace Affiant.Abstractions.Models;

using Affiant.Abstractions.Interfaces;

public sealed record InferenceCompletionRequest(
    IReadOnlyList<AffiantChatMessage> History,
    ITaskInferenceStrategy Strategy,
    string FunctionName,
    IReadOnlyDictionary<string, object?> Arguments);
