namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IAffidavitProjection
{
    string EntityType { get; }

    Affidavit Project(
        IContextFabric fabric,
        string operationType,
        IReadOnlyList<string> warnings);
}
