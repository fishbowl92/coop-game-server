using CoopGameServer.Admin.Services;

namespace CoopGameServer.UnitTests.Admin;

public sealed class AdminRewardSubmissionStateTests
{
    [Fact]
    public void RetryKeepsOriginalTargetAdministratorAndPayloadEvenWhenDraftChanges()
    {
        var state = new AdminRewardSubmissionState
        {
            GoldAmount = 100,
            ItemId = 7001,
            ItemQuantity = 2,
            Reason = " support-retry ",
        };
        var playerId = Guid.NewGuid();
        var first = state.BeginSubmission(playerId, "admin");
        state.GoldAmount = 999;
        state.ItemId = 8001;
        state.ItemQuantity = 10;
        state.Reason = "edited";

        var retry = state.BeginSubmission(playerId, "admin");

        Assert.Same(first, retry);
        Assert.Equal(playerId, retry.PlayerId);
        Assert.Equal("admin", retry.AdministratorLoginId);
        Assert.Equal(100, retry.Request.GoldAmount);
        Assert.Equal(7001, retry.Request.ItemId);
        Assert.Equal(2, retry.Request.ItemQuantity);
        Assert.Equal("support-retry", retry.Request.Reason);
        Assert.Throws<InvalidOperationException>(() => state.BeginSubmission(Guid.NewGuid(), "admin"));
        Assert.Throws<InvalidOperationException>(() => state.BeginSubmission(playerId, "other-admin"));
    }

    [Fact]
    public void ConfirmSuccessCreatesNewRequestIdAndClearsPendingAndDraft()
    {
        var state = new AdminRewardSubmissionState
        {
            GoldAmount = 100,
            ItemId = 7001,
            ItemQuantity = 2,
            Reason = "completed-request",
        };
        var pending = state.BeginSubmission(Guid.NewGuid(), "admin");

        state.ConfirmSuccess();

        Assert.NotEqual(pending.Request.RequestId, state.RequestId);
        Assert.Null(state.Pending);
        Assert.Equal(0, state.GoldAmount);
        Assert.Null(state.ItemId);
        Assert.Null(state.ItemQuantity);
        Assert.Equal(string.Empty, state.Reason);
    }
}
