using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Policies.Referrals;

/// <summary>
/// Base class for Referral rules that escalate approval to a different reviewer.
/// A Referral matches when the Affidavit meets the rule's conditions and a target
/// reviewer user ID is returned. The ReviewGate receives <see cref="ReviewRequirement.ReferralRequired"/>
/// and defers the entry, routing the Evidence Card to the referred-to user.
///
/// Subclass and implement <see cref="MatchesAsync"/> and <see cref="GetReferredToUserIdAsync"/>.
/// </summary>
public abstract class ReferralRuleBase : IApprovalPolicy
{
    protected readonly ILogger Logger;

    protected ReferralRuleBase(ILogger? logger = null)
    {
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Returns true if this Referral rule's conditions match the given Affidavit.
    /// </summary>
    protected abstract Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the ID of the user to whom approval is escalated.
    /// Return null or empty to pass — the chain continues to the next policy.
    /// </summary>
    protected abstract Task<string?> GetReferredToUserIdAsync(Affidavit affidavit, CancellationToken cancellationToken);

    public async Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        if (!await MatchesAsync(affidavit, cancellationToken).ConfigureAwait(false))
            return null;

        var referredToUserId = await GetReferredToUserIdAsync(affidavit, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(referredToUserId))
        {
            Logger.LogWarning(
                "Referral rule {Policy} matched but returned no reviewer user ID — skipping",
                GetType().Name);
            return null;
        }

        Logger.LogInformation(
            "Referral rule {Policy} escalating {FieldCount} fields to reviewer {ReviewerId}",
            GetType().Name, affidavit.Fields.Length, referredToUserId);

        return ReviewRequirement.ReferralRequired;
    }
}
