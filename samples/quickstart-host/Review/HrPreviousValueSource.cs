namespace QuickstartHost.Review;

using System.Globalization;
using Affiant.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;
using QuickstartHost.Agent;
using QuickstartHost.Data;

/// <summary>
/// What the host's own system of record holds for a leave request today (AF-3).
///
/// <para>
/// An update-shaped Affidavit carries, per field, the value the write would replace, so a reviewer
/// sees what is changing rather than only what is proposed. Only the host can answer that, which is
/// why the framework asks for this port rather than guessing.
/// </para>
///
/// <para>
/// A row the table does not hold is <b>not</b> an empty row: the answer is <c>null</c>, meaning
/// "nothing to project", and the projection swears the fields without previous values rather than
/// swearing that every one of them was blank.
/// </para>
/// </summary>
public sealed class HrPreviousValueSource(IServiceScopeFactory scopeFactory) : IPreviousValueSource
{
    public async Task<IReadOnlyDictionary<string, object?>?> GetPreviousValuesAsync(
        string entityType, string entityId, CancellationToken cancellationToken)
    {
        if (!string.Equals(entityType, LeaveTaskInferenceStrategy.LeaveRequestEntity, StringComparison.Ordinal))
            return null;

        if (!int.TryParse(entityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();
        var row = await db.LeaveRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (row is null)
            return null;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["employee"] = row.Employee,
            ["startDate"] = row.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = row.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["leaveType"] = row.LeaveType,
            ["days"] = row.Days,
            ["reason"] = row.Reason,
        };
    }
}
