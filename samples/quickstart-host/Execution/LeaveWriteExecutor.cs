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
/// <b>Amendments are already folded in.</b> This executor never merges an amendment map itself. The
/// gate folds a reviewer's accepted edits once, when the decision is recorded, and keeps the result
/// on <c>DocketEntry.AmendedAffidavit</c>; <see cref="Hubs.ChatHub.ApproveEntry"/> hands that record
/// here. The Affidavit therefore already states the two meanings a map carries: a cleared mandatory
/// field is present with no value, and a cleared optional field is absent from the field list
/// entirely because the write no longer proposes it. A second fold in here would be a second
/// implementation of the same merge, free to drift from the one the reviewer's card was built from.
/// </para>
/// </summary>
public sealed class LeaveWriteExecutor(HrDbContext db) : IWriteExecutor
{
    /// <param name="affidavit">
    /// The record to write, already amended: the gate's <c>AmendedAffidavit</c> when a reviewer
    /// corrected anything, the filed proposal when they did not.
    /// </param>
    /// <param name="amendments">
    /// Unread. The contract offers the raw map for a host that has not yet moved to the gate's own
    /// fold; this sample has, so its caller passes <c>null</c>.
    /// </param>
    /// <param name="ct">Cancels the database work.</param>
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

        record.Employee = ReadField(affidavit, "Employee") ?? record.Employee;
        record.StartDate = ParseDate(ReadField(affidavit, "StartDate"), record.StartDate);
        record.EndDate = ParseDate(ReadField(affidavit, "EndDate"), record.EndDate);
        record.LeaveType = ReadField(affidavit, "LeaveType") ?? record.LeaveType;
        record.Days = ParseInt(ReadField(affidavit, "Days"), record.Days);
        record.Reason = ReadField(affidavit, "Reason") ?? record.Reason;

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
    /// What the sworn record says this field should hold: its value, or the empty string when the
    /// field is proposed with no value — a reviewer cleared it, which this sample expresses as an
    /// empty string. A field the record does not propose at all reads <c>null</c>, and the caller
    /// leaves the row's current value alone.
    /// </summary>
    private static string? ReadField(Affidavit affidavit, string name)
    {
        var field = affidavit.Fields.FirstOrDefault(f => f.Name == name);
        return field is null ? null : field.Value?.ToString() ?? string.Empty;
    }

    private static DateOnly ParseDate(string? value, DateOnly fallback) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
