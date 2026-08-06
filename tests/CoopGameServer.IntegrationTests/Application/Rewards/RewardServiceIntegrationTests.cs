using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Api.Domain.Players;
using CoopGameServer.Api.Domain.Wallets;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.Application.Rewards;

/// <summary>
/// 실제 PostgreSQL·EF Core·트랜잭션·UNIQUE 제약 조건을 함께 사용하는 보상 서비스 통합 테스트입니다.
/// </summary>
[Collection(PostgreSqlIntegrationTestGroup.Name)]
public sealed class RewardServiceIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlDatabaseFixture _databaseFixture;

    /// <summary>
    /// xUnit이 공유하는 테스트 전용 PostgreSQL Fixture를 주입받습니다.
    /// </summary>
    /// <param name="databaseFixture">컨테이너 시작, 마이그레이션, 데이터 초기화를 담당합니다.</param>
    public RewardServiceIntegrationTests(PostgreSqlDatabaseFixture databaseFixture)
    {
        _databaseFixture = databaseFixture;
    }

    /// <summary>
    /// 각 테스트를 시작하기 전에 이전 테스트의 데이터만 비웁니다.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _databaseFixture.ResetDataAsync();
    }

    /// <summary>
    /// 테스트별로 별도 정리할 리소스는 없으며 Fixture가 컨테이너 전체를 정리합니다.
    /// </summary>
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 같은 멱등성 키의 요청 100개가 동시에 들어와도 보상이 정확히 한 번만 적용되는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task GrantAsyncWithOneHundredConcurrentSameRequestIdAppliesRewardExactlyOnce()
    {
        var player = await CreatePlayerAsync("ParallelRewardPlayer");
        var request = new GrantRewardRequest(
            RequestId: Guid.NewGuid(),
            GoldAmount: 500,
            ItemId: 1001,
            ItemQuantity: 2,
            Reason: "parallel-idempotency-test");

        // 요청마다 별도의 DbContext와 DB 연결을 사용해야 실제 여러 HTTP 요청처럼 동시에 경합할 수 있습니다.
        var rewardTasks = Enumerable.Range(0, 100)
            .Select(_ => GrantRewardWithNewDbContextAsync(player.Id, request))
            .ToArray();

        var results = await Task.WhenAll(rewardTasks);

        Assert.DoesNotContain(results, result => result is null);

        var nonNullResults = results
            .Select(result => Assert.IsType<GrantRewardResult>(result))
            .ToArray();

        // 최초 적용은 정확히 한 번, 나머지 99개는 같은 결과를 읽어 온 재전송이어야 합니다.
        Assert.Single(nonNullResults, result => !result.IsReplay);
        Assert.Equal(99, nonNullResults.Count(result => result.IsReplay));

        await using var assertionDbContext = _databaseFixture.CreateDbContext();

        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var inventoryItem = await assertionDbContext.InventoryItems.SingleAsync(
            entity => entity.PlayerId == player.Id && entity.ItemId == 1001);
        var rewardAudits = await assertionDbContext.RewardAudits
            .Where(entity => entity.RequestId == request.RequestId)
            .ToListAsync();

        Assert.Equal(500, wallet.Gold);
        Assert.Equal(2, inventoryItem.Quantity);
        Assert.Single(rewardAudits);
    }

    /// <summary>
    /// 같은 키를 다른 지급 내용에 사용하면 기존 보상을 바꾸지 않고 충돌로 거부하는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task GrantAsyncRejectsDifferentRewardDataForExistingRequestId()
    {
        var player = await CreatePlayerAsync("ConflictRewardPlayer");
        var originalRequest = new GrantRewardRequest(
            RequestId: Guid.NewGuid(),
            GoldAmount: 500,
            ItemId: null,
            ItemQuantity: null,
            Reason: "original-reward");

        var originalResult = await GrantRewardWithNewDbContextAsync(player.Id, originalRequest);
        Assert.NotNull(originalResult);
        Assert.False(originalResult.IsReplay);

        var changedRequest = originalRequest with { GoldAmount = 999 };

        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(
            () => GrantRewardWithNewDbContextAsync(player.Id, changedRequest));

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var rewardAudits = await assertionDbContext.RewardAudits
            .Where(entity => entity.RequestId == originalRequest.RequestId)
            .ToListAsync();

        Assert.Equal(500, wallet.Gold);
        Assert.Single(rewardAudits);
        Assert.Equal(500, rewardAudits[0].GoldAmount);
    }

    /// <summary>
    /// 보상 이력 저장 후 지갑 갱신이 실패하면 모든 DB 변경이 롤백되는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task GrantAsyncRollsBackRewardAuditWhenWalletUpdateOverflows()
    {
        var player = await CreatePlayerAsync("RollbackRewardPlayer");
        var originalWallet = new PlayerWallet(player.Id, DateTimeOffset.UtcNow);
        originalWallet.AddGold(long.MaxValue, DateTimeOffset.UtcNow);

        await using (var setupDbContext = _databaseFixture.CreateDbContext())
        {
            setupDbContext.PlayerWallets.Add(originalWallet);
            await setupDbContext.SaveChangesAsync();
        }

        var request = new GrantRewardRequest(
            RequestId: Guid.NewGuid(),
            GoldAmount: 1,
            ItemId: null,
            ItemQuantity: null,
            Reason: "force-wallet-overflow");

        await Assert.ThrowsAsync<OverflowException>(
            () => GrantRewardWithNewDbContextAsync(player.Id, request));

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var rewardAudits = await assertionDbContext.RewardAudits
            .Where(entity => entity.RequestId == request.RequestId)
            .ToListAsync();

        // reward_audits는 지갑 변경보다 먼저 SaveChangesAsync를 했지만 같은 트랜잭션이므로 함께 취소되어야 합니다.
        Assert.Equal(long.MaxValue, wallet.Gold);
        Assert.Empty(rewardAudits);
    }

    /// <summary>
    /// 실제 PostgreSQL에 테스트용 플레이어 한 명을 저장합니다.
    /// </summary>
    private async Task<Player> CreatePlayerAsync(string nickname)
    {
        var player = new Player(Guid.NewGuid(), nickname, DateTimeOffset.UtcNow);

        await using var gameDbContext = _databaseFixture.CreateDbContext();
        gameDbContext.Players.Add(player);
        await gameDbContext.SaveChangesAsync();

        return player;
    }

    /// <summary>
    /// 각각 다른 DbContext를 만들어 실제 동시 요청과 같은 DB 연결 경합을 재현합니다.
    /// </summary>
    private async Task<GrantRewardResult?> GrantRewardWithNewDbContextAsync(
        Guid playerId,
        GrantRewardRequest request)
    {
        await using var gameDbContext = _databaseFixture.CreateDbContext();
        var rewardService = new RewardService(gameDbContext);

        return await rewardService.GrantAsync(playerId, request, CancellationToken.None);
    }
}
