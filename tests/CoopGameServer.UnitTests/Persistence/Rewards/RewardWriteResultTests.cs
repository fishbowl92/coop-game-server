using CoopGameServer.Persistence.Rewards;

namespace CoopGameServer.UnitTests.PersistenceLayer.Rewards;

/// <summary>
/// 보상 쓰기 결과가 성공·재생·실패 상태를 서로 모순 없이 표현하는지 검증합니다.
/// </summary>
public sealed class RewardWriteResultTests
{
    [Fact]
    public void AppliedContainsReceiptWithoutErrorOrReplayFlag()
    {
        var receipt = CreateReceipt();

        var result = RewardWriteResult.Applied(receipt);

        Assert.False(result.IsReplay);
        Assert.Equal(RewardWriteError.None, result.Error);
        Assert.Same(receipt, result.Receipt);
    }

    [Fact]
    public void ReplayedContainsReceiptAndReplayFlag()
    {
        var receipt = CreateReceipt();

        var result = RewardWriteResult.Replayed(receipt);

        Assert.True(result.IsReplay);
        Assert.Equal(RewardWriteError.None, result.Error);
        Assert.Same(receipt, result.Receipt);
    }

    [Fact]
    public void FailedRejectsNoneBecauseNoneIsNotAnError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RewardWriteResult.Failed(RewardWriteError.None));
    }

    [Fact]
    public void SuccessFactoriesRejectMissingReceipt()
    {
        Assert.Throws<ArgumentNullException>(
            () => RewardWriteResult.Applied(null!));
        Assert.Throws<ArgumentNullException>(
            () => RewardWriteResult.Replayed(null!));
    }

    [Fact]
    public void FailedRejectsUndefinedErrorValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RewardWriteResult.Failed((RewardWriteError)999));
    }

    /// <summary>팩터리 메서드 검증에 사용할 임의의 정상 영수증을 만듭니다.</summary>
    private static RewardWriteReceipt CreateReceipt()
    {
        return new RewardWriteReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            100,
            null,
            null,
            "result-invariant-test",
            DateTimeOffset.UtcNow);
    }
}
