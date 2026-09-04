namespace Affiant.Policies.Tests.Referrals;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Policies.Referrals;
using Xunit;

public class ReferralRulePolicyTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class ConfigurableReferralRule : ReferralRuleBase
    {
        // Parameterless because ReferralRuleBase(ILogger? logger = null) is effectively parameterless.
        public bool ShouldMatch { get; init; } = true;
        public string? ReferToUserId { get; init; } = "manager-123";

        protected override Task<bool> MatchesAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(ShouldMatch);

        protected override Task<string?> GetReferredToUserIdAsync(Affidavit affidavit, CancellationToken ct)
            => Task.FromResult(ReferToUserId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Affidavit MakeAffidavit() => new(
        OperationType: "Test",
        EntityType: "TestEntity",
        EntityId: null,
        Fields: [],
        AggregateConfidence: 1.0f,
        PopulatedConfidence: 1.0f,
        EmptyFieldCount: 0,
        Warnings: [],
        RequiresConfirmation: false);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_returns_null_when_conditions_do_not_match()
    {
        var rule = new ConfigurableReferralRule { ShouldMatch = false };

        var result = await rule.EvaluateAsync(MakeAffidavit());

        Assert.Null(result);
    }

    [Fact]
    public async Task EvaluateAsync_returns_ReferralRequired_when_matches_and_user_found()
    {
        var rule = new ConfigurableReferralRule { ShouldMatch = true, ReferToUserId = "manager-123" };

        var result = await rule.EvaluateAsync(MakeAffidavit());

        Assert.NotNull(result);
        Assert.Equal(ReviewRequirement.ReferralRequired, result);
    }

    [Fact]
    public async Task EvaluateAsync_returns_null_when_user_id_is_null()
    {
        var rule = new ConfigurableReferralRule { ShouldMatch = true, ReferToUserId = null };

        var result = await rule.EvaluateAsync(MakeAffidavit());

        Assert.Null(result);
    }

    [Fact]
    public async Task EvaluateAsync_returns_null_when_user_id_is_empty()
    {
        var rule = new ConfigurableReferralRule { ShouldMatch = true, ReferToUserId = "" };

        var result = await rule.EvaluateAsync(MakeAffidavit());

        Assert.Null(result);
    }

    [Fact]
    public async Task EvaluateAsync_routes_to_different_users_for_different_rules()
    {
        var seniorReviewer = new ConfigurableReferralRule { ShouldMatch = true, ReferToUserId = "senior-1" };
        var complianceReviewer = new ConfigurableReferralRule { ShouldMatch = true, ReferToUserId = "compliance-2" };
        var affidavit = MakeAffidavit();

        var result1 = await seniorReviewer.EvaluateAsync(affidavit);
        var result2 = await complianceReviewer.EvaluateAsync(affidavit);

        // Both return ReferralRequired; the ReviewerUserId is carried by the Docket entry
        // update in the host, not by the enum value itself.
        Assert.Equal(ReviewRequirement.ReferralRequired, result1);
        Assert.Equal(ReviewRequirement.ReferralRequired, result2);
    }
}
