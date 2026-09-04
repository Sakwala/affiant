namespace QuickstartHost.Agent;

using System.ComponentModel;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Models;
using Microsoft.SemanticKernel;

/// <summary>
/// The update half of this sample's write domain: proposes moving an existing leave request's end
/// date.
///
/// This is the tool the sample exists to show. A create has nothing to compare against, so its
/// affidavit is a list of proposed values. An update does: the projection loads the row being
/// changed, so the reviewer's card carries the entity's id and, per field, the value the database
/// holds right now — and can therefore show that four of the five fields are unchanged and one is
/// not. Same tool shape, same strategy, same projection; the only difference is that this call
/// names a row.
/// </summary>
public sealed class AmendLeavePlugin(LeaveProposalBuilder proposals)
{
    /// <summary>The SK function name; the same string the tool descriptor is registered under.</summary>
    public const string FunctionName = "amend_leave";

    [KernelFunction(FunctionName)]
    [AffiantWriteTool("WriteUpdate", LeaveTaskInferenceStrategy.LeaveRequestEntity, typeof(LeaveTaskInferenceStrategy))]
    [Description("Propose a change to the end date of an existing leave request. Returns a proposal for a human to review; never writes to the database.")]
    public Task<string> AmendLeaveAsync(
        [Description("The id of the leave request to change.")] int leaveRequestId,
        [Description("The new last day of leave, inclusive, as yyyy-MM-dd.")] string endDate)
    {
        var affidavit = proposals.BuildUpdate(
            leaveRequestId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["EndDate"] = endDate });

        if (affidavit.EntityId is null)
        {
            return Task.FromResult(new ToolError(
                ToolName: FunctionName,
                Timestamp: DateTimeOffset.UtcNow,
                Code: "leave_request_not_found",
                Message: $"No leave request exists with id {leaveRequestId}. List the requests first and use an id from that list.",
                Retryable: false).ToJsonString());
        }

        return Task.FromResult(
            new WriteProposal(FunctionName, DateTimeOffset.UtcNow, affidavit).ToJsonString());
    }
}
