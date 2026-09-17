using CoopGameServer.Admin.Services;

namespace CoopGameServer.UnitTests.Admin;

public sealed class AdminRewardSubmissionStateTests
{
    [Fact]
    public void CreateRequestReusesSameRequestIdUntilSuccessIsConfirmed()
    {
        var state = new AdminRewardSubmissionState
        {
            GoldAmount = 100,
            ItemId = 7001,
            ItemQuantity = 2,
            Reason = "support-retry",
        };

        var firstAttempt = state.CreateRequest();
        var retryAttempt = state.CreateRequest();

        Assert.Equal(firstAttempt.RequestId, retryAttempt.RequestId);
        Assert.Equal(100, retryAttempt.GoldAmount);
        Assert.Equal("support-retry", retryAttempt.Reason);
    }

    [Fact]
    public void ConfirmSuccessCreatesNewRequestIdAndClearsPreviousPayload()
    {
        var state = new AdminRewardSubmissionState
        {
            GoldAmount = 100,
            ItemId = 7001,
            ItemQuantity = 2,
            Reason = "completed-request",
        };
        var completedRequestId = state.RequestId;

        state.ConfirmSuccess();

        Assert.NotEqual(completedRequestId, state.RequestId);
        Assert.Equal(0, state.GoldAmount);
        Assert.Null(state.ItemId);
        Assert.Null(state.ItemQuantity);
        Assert.Equal(string.Empty, state.Reason);
    }
}
