using System.Text.Json.Nodes;
using Affiant.Abstractions.Interfaces;

namespace Affiant.Conformance.Tests.Ports;

/// <summary>
/// What the host's entities hold now (AF-3), from <c>given.entities</c> keyed
/// <c>"&lt;entityType&gt;/&lt;entityId&gt;"</c>.
/// </summary>
/// <remarks>
/// An entity the table does not name <b>does not exist</b>, and this port answers "nothing to
/// project" — which is not the same as "every field was empty", and is why the answer is
/// <c>null</c> rather than an empty map.
/// </remarks>
internal sealed class FixtureEntities(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode?>> entities)
    : IPreviousValueSource
{
    public Task<IReadOnlyDictionary<string, object?>?> GetPreviousValuesAsync(
        string entityType, string entityId, CancellationToken cancellationToken)
    {
        if (!entities.TryGetValue($"{entityType}/{entityId}", out var held))
            return Task.FromResult<IReadOnlyDictionary<string, object?>?>(null);

        IReadOnlyDictionary<string, object?> values = held.ToDictionary(
            kv => kv.Key, kv => Values.ToClr(kv.Value), StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, object?>?>(values);
    }
}
