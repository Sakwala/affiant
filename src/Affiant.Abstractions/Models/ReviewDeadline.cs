namespace Affiant.Abstractions.Models;

/// <summary>
/// What counts as a review deadline (protocol rule GT-4): a window at least one millisecond long
/// that can actually be stamped on an entry.
///
/// <para>
/// One definition, three callers, because the failure it prevents is the quietest one in the gate.
/// A window of zero — or of ten thousand years — files a Docket row that satisfies every other
/// invariant and reads expired on the read that files it, so the write the gate was standing in
/// front of simply never happens and nothing anywhere reports a failure. The framework's wire-up
/// validator holds the gate's own default to this rule, and the approval-policy chain holds a
/// policy's verdict and a policy's declared default to the same one — worded identically, so a host
/// that hits it in either place reads the same sentence.
/// </para>
/// </summary>
public static class ReviewDeadline
{
    /// <summary>The shortest window that is a window at all.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Whether <paramref name="timeToLive"/> is a deadline an entry could be stamped with, and — when
    /// it is not — which half of the rule it broke, as a sentence fragment.
    /// </summary>
    /// <param name="timeToLive">The proposed review window.</param>
    /// <param name="now">
    /// The instant the window would be measured from. Passed in rather than read here so a caller
    /// with an injectable clock keeps it.
    /// </param>
    /// <param name="reason">
    /// Why it is not a deadline, or <see langword="null"/> when it is one.
    /// </param>
    public static bool IsUsable(TimeSpan timeToLive, DateTimeOffset now, out string? reason)
    {
        if (timeToLive < Minimum)
        {
            reason = "a review window must be at least one millisecond";
            return false;
        }

        if (timeToLive > DateTimeOffset.MaxValue - now)
        {
            reason = "the deadline is too far in the future to stamp on an entry";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// The refusal message for a policy that named an unusable window, so the wire-up refusal and
    /// the per-request one read alike.
    /// </summary>
    /// <param name="policyId">The policy that named it.</param>
    /// <param name="option">Which half of the policy's contract named it.</param>
    /// <param name="timeToLive">The value it named.</param>
    /// <param name="reason">The fragment <see cref="IsUsable"/> produced.</param>
    public static string UnusableMessage(
        string policyId, string option, TimeSpan timeToLive, string reason) =>
        $"GT-4: approval policy '{policyId}' named {option} = {timeToLive}, which is not a review " +
        $"deadline — {reason}. A zero or negative window files an entry already past its deadline, " +
        "which no person can ever decide and which no rule would show as a failure; an unstampable " +
        "one has no instant to write at all. Nothing was filed.";
}
