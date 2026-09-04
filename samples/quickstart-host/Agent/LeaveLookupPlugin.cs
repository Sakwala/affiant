namespace QuickstartHost.Agent;

using System.ComponentModel;
using System.Globalization;
using System.Text;
using Affiant.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using QuickstartHost.Data;

/// <summary>
/// A read tool, present for two reasons: <c>amend_leave</c> needs a way for the model to learn a
/// row's id, and Rule 2 — dual-audience tool returns — is easier to see next to a write tool than
/// described. A read returns markdown the model can quote and <c>EntityRef</c> values the
/// framework's context extraction can consume; a write returns an affidavit.
///
/// Read tools are declared to the framework too, with <c>AddAffiantReadTool</c>. Every
/// <c>[KernelFunction]</c> the kernel exposes must have a descriptor or the startup validator
/// refuses to start the host.
///
/// <para>
/// It takes a scope factory rather than a <c>DbContext</c> because Semantic Kernel builds a plugin
/// instance once, from the root service provider: a plugin that injects a scoped service does not
/// start. A scope per call is the cost of that, and it is the right lifetime for a read anyway.
/// </para>
/// </summary>
public sealed class LeaveLookupPlugin(IServiceScopeFactory scopeFactory)
{
    /// <summary>The SK function name; the same string the tool descriptor is registered under.</summary>
    public const string FunctionName = "list_leave_requests";

    [KernelFunction(FunctionName)]
    [Description("List the leave requests already recorded, with their ids, so a request can be referred to by id.")]
    public async Task<string> ListLeaveRequestsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();

        var requests = await db.LeaveRequests
            .AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(20)
            .ToListAsync(cancellationToken);

        var markdown = new StringBuilder();
        if (requests.Count == 0)
        {
            markdown.Append("No leave requests have been recorded yet.");
        }
        else
        {
            markdown.AppendLine("| Id | Employee | Type | Start | End | Status |");
            markdown.AppendLine("|---|---|---|---|---|---|");
            foreach (var r in requests)
            {
                markdown.AppendLine(CultureInfo.InvariantCulture,
                    $"| {r.Id} | {r.Employee} | {r.LeaveType} | {r.StartDate:yyyy-MM-dd} | {r.EndDate:yyyy-MM-dd} | {r.Status} |");
            }
        }

        var entities = requests
            .Select(r => new EntityRef(
                EntityType: LeaveTaskInferenceStrategy.LeaveRequestEntity,
                EntityId: r.Id.ToString(CultureInfo.InvariantCulture),
                DisplayName: $"{r.Employee}, {r.LeaveType} {r.StartDate:yyyy-MM-dd} to {r.EndDate:yyyy-MM-dd}",
                Fields: new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Employee"] = r.Employee,
                    ["StartDate"] = r.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["EndDate"] = r.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["LeaveType"] = r.LeaveType,
                    ["Days"] = r.Days.ToString(CultureInfo.InvariantCulture),
                    ["Reason"] = r.Reason,
                }))
            .ToArray();

        return new ReadResult(
            ToolName: FunctionName,
            Timestamp: DateTimeOffset.UtcNow,
            Summary: $"{requests.Count} leave request(s).",
            Markdown: markdown.ToString(),
            Entities: entities).ToJsonString();
    }
}
