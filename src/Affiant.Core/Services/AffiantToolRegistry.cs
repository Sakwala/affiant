using System.Collections.Concurrent;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

namespace Affiant.Core.Services;

public sealed class AffiantToolRegistry : IAffiantToolRegistry
{
    private readonly ConcurrentDictionary<(string FunctionName, string? PluginName), AffiantToolDescriptor> _byKey = new();

    public void Register(AffiantToolDescriptor descriptor)
    {
        var key = (descriptor.FunctionName, descriptor.PluginName);
        if (!_byKey.TryAdd(key, descriptor))
        {
            var existing = _byKey[key];
            throw new InvalidOperationException(
                $"Tool descriptor for ({descriptor.FunctionName}, {descriptor.PluginName ?? "<null>"}) is already registered. " +
                $"Existing: {existing}. Incoming: {descriptor}.");
        }
    }

    public AffiantToolDescriptor? Find(string functionName, string? pluginName = null)
    {
        if (pluginName is not null)
        {
            return _byKey.TryGetValue((functionName, pluginName), out var exact) ? exact : null;
        }

        AffiantToolDescriptor? match = null;
        foreach (var kvp in _byKey)
        {
            if (kvp.Key.FunctionName != functionName) continue;
            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Lookup for function '{functionName}' is ambiguous: descriptors exist for multiple plugins " +
                    $"({match.PluginName ?? "<null>"} and {kvp.Value.PluginName ?? "<null>"}). " +
                    "Pass a non-null pluginName to Find.");
            }
            match = kvp.Value;
        }
        return match;
    }

    public IReadOnlyList<AffiantToolDescriptor> All => _byKey.Values.ToArray();
}
