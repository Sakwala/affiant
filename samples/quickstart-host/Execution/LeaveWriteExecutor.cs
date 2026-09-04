namespace QuickstartHost.Execution;

using System.Globalization;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using QuickstartHost.Agent;
using QuickstartHost.Data;

/// <summary>
/// The host's domain write port: the only code in this sample that changes a leave request. It
/// runs after a human approves, called from <see cref="Hubs.ChatHub"/>. Nothing in the framework
/// calls it for you — that boundary is deliberate, and it is why <c>SaveChanges</c> appears
/// exactly once in this sample, here.
///
/// <para>
/// <b>Amendments.</b> A reviewer's edits arrive alongside the affidavit. A key present with a
/// <c>null</c> value means the reviewer cleared that field, which is different from the key being
/// absent (leave it alone) — <see cref="ReadField"/> keeps the two apart. The framework has
/// already persisted these onto the docket entry; a host with its own audit trail would also
/// append a <c>UserStated</c> provenance tag per amended field before the value lands.
/// </para>
/// </summary>
public sealed class LeaveWriteExecutor(HrDbContext db) : IWriteExecutor
{
    public async Task<string?> ExecuteAsync(
        Affidavit affidavit,
        IReadOnlyDictionary<string, object?>? amendments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(affidavit);

        if (affidavit.EntityType != LeaveTaskInferenceStrategy.LeaveRequestEntity)
        {
            throw new NotSupportedException(
                $"No executor for entity type '{affidavit.EntityType}'.");
        }

        var record = await ResolveRecordAsync(affidavit, ct);

        record.Employee = ReadField(affidavit, amendments, "Employee") ?? record.Employee;
        record.StartDate = ParseDate(ReadField(affidavit, amendments, "StartDate"), record.StartDate);
        record.EndDate = ParseDate(ReadField(affidavit, amendments, "EndDate"), record.EndDate);
        record.LeaveType = ReadField(affidavit, amendments, "LeaveType") ?? record.LeaveType;
        record.Days = ParseInt(ReadField(affidavit, amendments, "Days"), record.Days);
        record.Reason = ReadField(affidavit, amendments, "Reason") ?? record.Reason;

        // SaveChanges happens ONLY here — never in a write tool, never in the projection.
        await db.SaveChangesAsync(ct);
        return record.Id.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<LeaveRequest> ResolveRecordAsync(Affidavit affidavit, CancellationToken ct)
    {
        if (affidavit.EntityId is null)
        {
            var created = new LeaveRequest { Status = "Submitted" };
            db.LeaveRequests.Add(created);
            return created;
        }

        if (!int.TryParse(affidavit.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            throw new InvalidOperationException(
                $"Affidavit.EntityId '{affidavit.EntityId}' is not a leave-request id.");
        }

        return await db.LeaveRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new InvalidOperationException($"Leave request {id} no longer exists.");
    }

    /// <summary>
    /// A reviewer's amendment wins over the sworn value; an amendment present with a <c>null</c>
    /// value clears the field, which this sample expresses as an empty string. A field the
    /// reviewer did not touch falls back to the affidavit's own value.
    /// </summary>
    private static string? ReadField(
        Affidavit affidavit,
        IReadOnlyDictionary<string, object?>? amendments,
        string name)
    {
        if (amendments is not null && amendments.TryGetValue(name, out var amended))
            return amended?.ToString() ?? string.Empty;

        var field = affidavit.Fields.FirstOrDefault(f => f.Name == name);
        return field?.Value?.ToString();
    }

    private static DateOnly ParseDate(string? value, DateOnly fallback) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
