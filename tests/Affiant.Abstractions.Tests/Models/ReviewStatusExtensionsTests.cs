namespace Affiant.Abstractions.Tests.Models;

using System;
using Affiant.Abstractions.Models;
using Xunit;

public sealed class ReviewStatusExtensionsTests
{
    private static readonly Guid DocketId = Guid.Parse("00000000-0000-0000-0000-0000000000a5");

    public static TheoryData<ReviewStatus> AllReviewStatuses()
    {
        var data = new TheoryData<ReviewStatus>();
        foreach (var status in Enum.GetValues<ReviewStatus>())
            data.Add(status);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllReviewStatuses))]
    public void ToReviewOutcome_MapsEveryLiveReviewStatus(ReviewStatus status)
    {
        var outcome = status.ToReviewOutcome(DocketId);

        Assert.Equal(DocketId, outcome.DocketId);
    }

    [Fact]
    public void ToReviewOutcome_Pending_MapsToExpired()
    {
        var outcome = ReviewStatus.Pending.ToReviewOutcome(DocketId);

        var expired = Assert.IsType<ReviewOutcome.Expired>(outcome);
        Assert.False(expired.AmendmentsPreserved);
    }

    [Fact]
    public void ToReviewOutcome_Approved_MapsToApproved()
    {
        var outcome = ReviewStatus.Approved.ToReviewOutcome(DocketId);

        Assert.IsType<ReviewOutcome.Approved>(outcome);
    }

    [Fact]
    public void ToReviewOutcome_Rejected_MapsToRejected()
    {
        var outcome = ReviewStatus.Rejected.ToReviewOutcome(DocketId);

        Assert.IsType<ReviewOutcome.Rejected>(outcome);
    }

    [Fact]
    public void ToReviewOutcome_Expired_MapsToExpired()
    {
        var outcome = ReviewStatus.Expired.ToReviewOutcome(DocketId);

        Assert.IsType<ReviewOutcome.Expired>(outcome);
    }

    [Fact]
    public void ToReviewOutcome_Deferred_MapsToReferral_WithDeferredEscalationPath()
    {
        var outcome = ReviewStatus.Deferred.ToReviewOutcome(DocketId);

        var referral = Assert.IsType<ReviewOutcome.Referral>(outcome);
        Assert.Equal("deferred", referral.EscalationPath);
    }

    [Fact]
    public void AllReviewStatuses_CoversExactlyTheFiveLiveMembers()
    {
        var members = Enum.GetValues<ReviewStatus>();
        Assert.Equal(
            [ReviewStatus.Pending, ReviewStatus.Approved, ReviewStatus.Rejected, ReviewStatus.Expired, ReviewStatus.Deferred],
            members);
    }
}
