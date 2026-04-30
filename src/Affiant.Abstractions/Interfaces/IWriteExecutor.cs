namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IWriteExecutor
{
    /// <summary>
    /// Execute an approved write operation.
    /// Use the Affidavit.OperationType to route to the correct domain handler,
    /// apply any amendments, then persist. Raise on failure — the ReviewGate does not retry.
    /// </summary>
    Task<string?> ExecuteAsync(Affidavit affidavit, Dictionary<string, object>? amendments, CancellationToken ct);
}
