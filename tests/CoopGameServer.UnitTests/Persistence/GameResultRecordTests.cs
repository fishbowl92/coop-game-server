using CoopGameServer.Persistence.GameRooms;

namespace CoopGameServer.UnitTests.Persistence.GameRooms;

/// <summary>Player별 게임 결과 전달 상태가 허용된 방향으로만 전이되는지 검증합니다.</summary>
public sealed class GameResultRecordTests
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        28,
        10,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void ConstructorCreatesPendingResultWithoutAttemptHistory()
    {
        var result = CreateResult();

        Assert.Equal(GameResultDeliveryStatus.Pending, result.DeliveryStatus);
        Assert.Equal(0, result.AttemptCount);
        Assert.Null(result.NextAttemptAt);
        Assert.Null(result.LastErrorCode);
        Assert.Equal(CreatedAt, result.UpdatedAt);
    }

    [Fact]
    public void AppliedAndNoRewardBecomeFinalSuccessfulStates()
    {
        var applied = CreateResult();
        var noReward = CreateResult();
        var completedAt = CreatedAt.AddSeconds(1);

        applied.MarkApplied(completedAt);
        noReward.MarkNoReward(completedAt);

        AssertFinalState(applied, GameResultDeliveryStatus.Applied, completedAt);
        AssertFinalState(noReward, GameResultDeliveryStatus.NoReward, completedAt);
        Assert.Throws<InvalidOperationException>(() => applied.MarkApplied(completedAt.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => noReward.MarkNoReward(completedAt.AddSeconds(1)));
    }

    [Fact]
    public void ScheduleRetryRecordsFailureAndAllowsLaterSuccessfulAttempt()
    {
        var result = CreateResult();
        var failedAt = CreatedAt.AddSeconds(1);
        var nextAttemptAt = failedAt.AddSeconds(5);

        result.ScheduleRetry("  TimeoutException  ", nextAttemptAt, failedAt);

        Assert.Equal(GameResultDeliveryStatus.PendingRetry, result.DeliveryStatus);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(nextAttemptAt, result.NextAttemptAt);
        Assert.Equal("TimeoutException", result.LastErrorCode);
        Assert.Equal(failedAt, result.UpdatedAt);

        var completedAt = nextAttemptAt.AddSeconds(1);
        result.MarkApplied(completedAt);

        AssertFinalState(result, GameResultDeliveryStatus.Applied, completedAt, expectedAttemptCount: 2);
    }

    [Fact]
    public void TerminalFailureStoresErrorAndCannotBeRetried()
    {
        var result = CreateResult();
        var failedAt = CreatedAt.AddSeconds(1);

        result.MarkTerminalFailure("PlayerNotFound", failedAt);

        Assert.Equal(GameResultDeliveryStatus.TerminalFailure, result.DeliveryStatus);
        Assert.Equal(1, result.AttemptCount);
        Assert.Null(result.NextAttemptAt);
        Assert.Equal("PlayerNotFound", result.LastErrorCode);
        Assert.Equal(failedAt, result.UpdatedAt);
        Assert.Throws<InvalidOperationException>(
            () => result.ScheduleRetry("TimeoutException", failedAt.AddSeconds(5), failedAt));
    }

    [Fact]
    public void RetryAndFailureRejectInvalidErrorOrTimeValues()
    {
        var result = CreateResult();
        var failedAt = CreatedAt.AddSeconds(1);

        Assert.Throws<ArgumentException>(
            () => result.MarkTerminalFailure("   ", failedAt));
        Assert.Throws<ArgumentException>(
            () => result.MarkTerminalFailure(
                new string('x', GameResultRecord.MaxLastErrorCodeLength + 1),
                failedAt));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => result.ScheduleRetry("TimeoutException", failedAt, failedAt));

        // 실패한 검증은 객체 상태를 일부 변경하지 않아야 합니다.
        Assert.Equal(GameResultDeliveryStatus.Pending, result.DeliveryStatus);
        Assert.Equal(0, result.AttemptCount);
    }

    private static GameResultRecord CreateResult()
    {
        return new GameResultRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            rewardPolicyVersion: 1,
            Guid.NewGuid(),
            CreatedAt);
    }

    private static void AssertFinalState(
        GameResultRecord result,
        GameResultDeliveryStatus expectedStatus,
        DateTimeOffset expectedUpdatedAt,
        int expectedAttemptCount = 1)
    {
        Assert.Equal(expectedStatus, result.DeliveryStatus);
        Assert.Equal(expectedAttemptCount, result.AttemptCount);
        Assert.Null(result.NextAttemptAt);
        Assert.Null(result.LastErrorCode);
        Assert.Equal(expectedUpdatedAt, result.UpdatedAt);
    }
}
