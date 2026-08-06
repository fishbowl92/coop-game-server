using CoopGameServer.Api.Domain.Wallets;

namespace CoopGameServer.UnitTests.Domain.Wallets;

/// <summary>
/// 플레이어 지갑의 골드 잔액 규칙을 검증합니다.
/// </summary>
public sealed class PlayerWalletTests
{
    [Fact]
    public void ConstructorCreatesEmptyWalletForPlayer()
    {
        var playerId = Guid.Parse("7c3fc9a4-5478-4e26-bda6-6eb6a4d5a9b2");
        var createdAt = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);

        var wallet = new PlayerWallet(playerId, createdAt);

        Assert.Equal(playerId, wallet.PlayerId);
        Assert.Equal(0, wallet.Gold);
        Assert.Equal(createdAt, wallet.UpdatedAt);
    }

    [Fact]
    public void AddGoldIncreasesBalanceAndUpdatesTimestamp()
    {
        var createdAt = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(3);
        var wallet = new PlayerWallet(Guid.NewGuid(), createdAt);

        wallet.AddGold(250, updatedAt);

        Assert.Equal(250, wallet.Gold);
        Assert.Equal(updatedAt, wallet.UpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddGoldRejectsNonPositiveAmount(long amount)
    {
        var wallet = new PlayerWallet(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => wallet.AddGold(amount, DateTimeOffset.UtcNow));
    }
}
