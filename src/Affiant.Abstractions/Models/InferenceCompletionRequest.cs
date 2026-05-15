namespace Affiant.Abstractions.Models;

using Affiant.Abstractions.Interfaces;
using Microsoft.SemanticKernel.ChatCompletion;

public sealed record InferenceCompletionRequest(
    ChatHistory History,
    ITaskInferenceStrategy Strategy,
    string FunctionName,
    IReadOnlyDictionary<string, object?> Arguments);
