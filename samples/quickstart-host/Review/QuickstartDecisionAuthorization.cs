namespace QuickstartHost.Review;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// Whether a principal may decide a given Docket entry (AZ-2) — the one question about the review
/// loop the framework cannot answer for a host.
///
/// <para>
/// The framework has already refused an unresolved principal and already refused an entry outside
/// the caller's tenant by the time this is asked, so what is left is the host's own rule. This
/// sample authenticates nobody and has exactly one reviewer, so the rule is "the demo reviewer, and
/// nobody else" — a machine caller included: a service principal is refused here rather than being
/// allowed to approve a write in a person's name.
/// </para>
///
/// <para>
/// A real host reads its own roles or ownership here. What it must not do is return <c>true</c>
/// when it could not decide: <c>false</c> and a throw both refuse, because a check that fell over
/// has not said yes.
/// </para>
/// </summary>
public sealed class QuickstartDecisionAuthorization : IDecisionAuthorizationPolicy
{
    public Task<bool> MayDecideAsync(
        Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return Task.FromResult(principal is Principal.Member member
            && string.Equals(member.Id, HttpReviewContextProvider.DemoUserId, StringComparison.Ordinal));
    }
}
