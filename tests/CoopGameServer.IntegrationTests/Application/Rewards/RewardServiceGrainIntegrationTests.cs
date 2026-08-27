using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.GrainContracts.Players;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.Application.Rewards;

/// <summary>
/// API 보상 어댑터가 실제 Orleans Client를 거쳐 PlayerGrain과 PostgreSQL까지 연결되는지 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class RewardServiceGrainIntegrationTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;

    [Fact]
    public async Task GrantAsyncTraversesPlayerGrainAndPersistsReward()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);

        // TestCluster Client도 IGrainFactory이므로 실제 API와 같은 Proxy 생성 경로를 사용합니다.
        var grainClient = new OrleansPlayerGrainClient(_fixture.Cluster.Client);
        var service = new RewardService(grainClient);
        var request = new GrantRewardRequest(
            requestId,
            GoldAmount: 250,
            ItemId: 4101,
            ItemQuantity: 4,
            Reason: "  api-to-player-grain  ");

        var result = await service.GrantAsync(playerId, request, CancellationToken.None);

        Assert.Equal(PlayerRewardCommandStatus.Applied, result.Status);
        Assert.Equal(PlayerRewardCommandError.None, result.Error);
        Assert.False(result.IsReplay);

        var receipt = Assert.IsType<PlayerRewardReceipt>(result.Receipt);
        Assert.Equal(requestId, receipt.RequestId);
        Assert.Equal(playerId, receipt.PlayerId);
        Assert.Equal("api-to-player-grain", receipt.Reason);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.Equal(
            250,
            (await gameDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == playerId)).Gold);
        Assert.Equal(
            4,
            (await gameDbContext.InventoryItems.SingleAsync(
                entity => entity.PlayerId == playerId && entity.ItemId == 4101)).Quantity);
        Assert.Equal(
            1,
            await gameDbContext.RewardAudits.CountAsync(entity => entity.RequestId == requestId));
    }
}
