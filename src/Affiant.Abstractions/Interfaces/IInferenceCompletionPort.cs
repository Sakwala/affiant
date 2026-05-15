namespace Affiant.Abstractions.Interfaces;

using System.Text.Json;
using Affiant.Abstractions.Models;

public interface IInferenceCompletionPort
{
    Task<JsonElement> CompleteStructuredAsync(
        InferenceCompletionRequest request,
        CancellationToken cancellationToken = default);
}
