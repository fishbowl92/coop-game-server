using CoopGameServer.Domain.Rewards;

namespace CoopGameServer.UnitTests.Domain.Rewards;

/// <summary>
/// 보상 감사 기록이 유효한 지급 정보만 보관하는지 검증합니다.
/// </summary>
public sealed class RewardAuditTests
{
    [Fact]
    public void ConstructorCreatesGoldOnlyRewardAuditWithTrimmedReason()
    {
        var id = Guid.Parse("3e208803-822b-49ef-93aa-a75c0d150ff6");
        var requestId = Guid.Parse("438525ff-8dbd-4a02-bd1f-53cc1763a43c");
        var playerId = Guid.Parse("6e1374b6-5a28-488a-afdb-5fd5d6c2f7a4");
        var createdAt = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);

        var rewardAudit = new RewardAudit(
            id,
            requestId,
            playerId,
            goldAmount: 500,
            itemId: null,
            itemQuantity: null,
            reason: "  daily-login  ",
            createdAt);

        Assert.Equal(id, rewardAudit.Id);
        Assert.Equal(requestId, rewardAudit.RequestId);
        Assert.Equal(playerId, rewardAudit.PlayerId);
        Assert.Equal(500, rewardAudit.GoldAmount);
        Assert.Null(rewardAudit.ItemId);
        Assert.Null(rewardAudit.ItemQuantity);
        Assert.Equal("daily-login", rewardAudit.Reason);
        Assert.Equal(createdAt, rewardAudit.CreatedAt);
    }

    [Fact]
    public void ConstructorAllowsItemOnlyRewardAudit()
    {
        var rewardAudit = new RewardAudit(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            goldAmount: 0,
            itemId: 1001,
            itemQuantity: 2,
            reason: "quest-clear",
            DateTimeOffset.UtcNow);

        Assert.Equal(1001, rewardAudit.ItemId);
        Assert.Equal(2, rewardAudit.ItemQuantity);
    }

    [Fact]
    public void ConstructorRejectsRewardWithoutGoldOrItem()
    {
        Assert.Throws<ArgumentException>(
            () => new RewardAudit(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                goldAmount: 0,
                itemId: null,
                itemQuantity: null,
                reason: "invalid",
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ConstructorRejectsItemQuantityWithoutItemId()
    {
        Assert.Throws<ArgumentException>(
            () => new RewardAudit(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                goldAmount: 100,
                itemId: null,
                itemQuantity: 1,
                reason: "invalid",
                DateTimeOffset.UtcNow));
    }
}
