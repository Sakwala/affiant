using Affiant.Abstractions.Models;

namespace Affiant.Abstractions.Interfaces;

public interface IAffiantToolRegistry
{
    void Register(AffiantToolDescriptor descriptor);
    AffiantToolDescriptor? Find(string functionName, string? pluginName = null);
    IReadOnlyList<AffiantToolDescriptor> All { get; }
}
