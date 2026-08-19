namespace Affiant.Abstractions.Models;

/// <summary>
/// The kind of approval a proposed mutation requires, as decided by the
/// <see cref="Interfaces.IApprovalPolicy"/> chain. Sibling to <see cref="ReviewStatus"/>: this is
/// what the policy chain demands up front, <c>ReviewStatus</c> is where the resulting docket entry
/// currently sits.
/// </summary>
public enum ReviewRequirement
{
    /// <summary>A standing order pre-authorizes the write; it is auto-approved by a named approver.</summary>
    StandingOrder,

    /// <summary>A human reviewer must confirm the write before it executes.</summary>
    ReviewerConfirmation,

    /// <summary>The write must be referred to a different, rule-selected reviewer.</summary>
    ReferralRequired,

    /// <summary>The write requires more than one party's approval.</summary>
    MultiParty
}
