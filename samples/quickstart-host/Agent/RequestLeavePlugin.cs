namespace QuickstartHost.Agent;

using System.ComponentModel;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Models;
using Microsoft.SemanticKernel;

/// <summary>
/// The create half of this sample's write domain: proposes a new leave request.
///
/// Rule 3 — write tools never write — is visible in the type's dependencies: there is no
/// <c>DbContext</c> here. The tool returns a <c>WriteProposal</c>; the only code in this sample
/// that touches the leave-request table is <see cref="Execution.LeaveWriteExecutor"/>, which runs
/// after a human approves.
/// </summary>
public sealed class RequestLeavePlugin(LeaveProposalBuilder proposals)
{
    /// <summary>The SK function name; the same string the tool descriptor is registered under.</summary>
    public const string FunctionName = "request_leave";

    [KernelFunction(FunctionName)]
    [AffiantWriteTool("WriteCreate", LeaveTaskInferenceStrategy.LeaveRequestEntity, typeof(LeaveTaskInferenceStrategy))]
    [Description("Propose a new leave request. Returns a proposal for a human to review; never writes to the database.")]
    public Task<string> RequestLeaveAsync(
        [Description("The employee's full name.")] string employee,
        [Description("First day of leave, as yyyy-MM-dd.")] string startDate,
        [Description("Last day of leave, inclusive, as yyyy-MM-dd.")] string endDate,
        [Description("Annual, Sick, or Personal.")] string leaveType,
        [Description("Working days this leave uses up.")] int days,
        [Description("Why the leave is being requested.")] string reason)
    {
        var affidavit = proposals.BuildCreate(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Employee"] = employee,
            ["StartDate"] = startDate,
            ["EndDate"] = endDate,
            ["LeaveType"] = leaveType,
            ["Days"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Reason"] = reason,
        });

        return Task.FromResult(
            new WriteProposal(FunctionName, DateTimeOffset.UtcNow, affidavit).ToJsonString());
    }
}
