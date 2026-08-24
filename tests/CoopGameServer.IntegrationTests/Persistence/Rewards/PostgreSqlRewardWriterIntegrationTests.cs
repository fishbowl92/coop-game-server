using CoopGameServer.Domain.Inventories;
using CoopGameServer.Domain.Players;
using CoopGameServer.Domain.Wallets;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence.Rewards;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.PersistenceLayer.Rewards;

/// <summary>
/// 실제 PostgreSQL·EF Core·트랜잭션·UNIQUE 제약 조건을 함께 사용하는 보상 Writer 통합 테스트입니다.
/// </summary>
[Collection(PostgreSqlIntegrationTestGroup.Name)]
public sealed class PostgreSqlRewardWriterIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlDatabaseFixture _databaseFixture;
    private readonly PostgreSqlRewardWriter _rewardWriter;

    /// <summary>
    /// xUnit이 공유하는 테스트 전용 PostgreSQL Fixture를 주입받습니다.
    /// </summary>
    /// <param name="databaseFixture">컨테이너 시작, 마이그레이션, 데이터 초기화를 담당합니다.</param>
    public PostgreSqlRewardWriterIntegrationTests(PostgreSqlDatabaseFixture databaseFixture)
    {
        _databaseFixture = databaseFixture;
        _rewardWriter = new PostgreSqlRewardWriter(databaseFixture, TimeProvider.System);
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
    public async Task WriteAsyncWithOneHundredConcurrentSameRequestIdAppliesRewardExactlyOnce()
    {
        var player = await CreatePlayerAsync("ParallelRewardPlayer");
        var command = new RewardWriteCommand(
            Guid.NewGuid(),
            player.Id,
            500,
            1001,
            2,
            "parallel-idempotency-test");

        // 같은 Singleton Writer를 공유해도 내부 Factory가 호출마다 별도의 DbContext와 DB 연결을 만들어야 합니다.
        var rewardTasks = Enumerable.Range(0, 100)
            .Select(_ => _rewardWriter.WriteAsync(command))
            .ToArray();

        var results = await Task.WhenAll(rewardTasks);

        Assert.All(results, result =>
        {
            Assert.Equal(RewardWriteError.None, result.Error);
            Assert.NotNull(result.Receipt);
        });

        // 최초 적용은 정확히 한 번, 나머지 99개는 같은 결과를 읽어 온 재전송이어야 합니다.
        Assert.Single(results, result => !result.IsReplay);
        Assert.Equal(99, results.Count(result => result.IsReplay));

        await using var assertionDbContext = _databaseFixture.CreateDbContext();

        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var inventoryItem = await assertionDbContext.InventoryItems.SingleAsync(
            entity => entity.PlayerId == player.Id && entity.ItemId == 1001);
        var rewardAudits = await assertionDbContext.RewardAudits
            .Where(entity => entity.RequestId == command.RequestId)
            .ToListAsync();

        Assert.Equal(500, wallet.Gold);
        Assert.Equal(2, inventoryItem.Quantity);
        Assert.Single(rewardAudits);
    }

    /// <summary>
    /// 아직 지갑과 인벤토리가 없는 플레이어에게 서로 다른 보상 요청이 동시에 도착해도
    /// 첫 행 생성이 충돌하지 않고 모든 보상이 누적되는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task WriteAsyncWithConcurrentDifferentRequestIdsCreatesBalancesOnceAndAccumulatesEveryReward()
    {
        const int concurrentRequestCount = 100;
        const int itemId = 2001;

        var player = await CreatePlayerAsync("FirstRacePlayer");
        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 모든 작업을 먼저 만든 뒤 같은 신호로 동시에 출발시켜 첫 지갑·인벤토리 행 생성 경쟁을 재현합니다.
        var rewardTasks = Enumerable.Range(0, concurrentRequestCount)
            .Select(index => WriteRewardAfterStartSignalAsync(
                new RewardWriteCommand(
                    Guid.NewGuid(),
                    player.Id,
                    1,
                    itemId,
                    1,
                    $"first-concurrent-reward-{index}"),
                startSignal.Task))
            .ToArray();

        startSignal.SetResult(true);
        var results = await Task.WhenAll(rewardTasks);

        Assert.All(results, result =>
        {
            Assert.Equal(RewardWriteError.None, result.Error);
            Assert.NotNull(result.Receipt);
            Assert.False(result.IsReplay);
        });

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var inventoryItem = await assertionDbContext.InventoryItems.SingleAsync(
            entity => entity.PlayerId == player.Id && entity.ItemId == itemId);
        var rewardAuditCount = await assertionDbContext.RewardAudits
            .CountAsync(entity => entity.PlayerId == player.Id);

        Assert.Equal(concurrentRequestCount, wallet.Gold);
        Assert.Equal(concurrentRequestCount, inventoryItem.Quantity);
        Assert.Equal(concurrentRequestCount, rewardAuditCount);
    }

    /// <summary>
    /// 이미 존재하는 지갑과 인벤토리에 서로 다른 요청이 동시에 보상을 추가해도
    /// 읽기-수정-쓰기 경합으로 누적값이 유실되지 않는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task WriteAsyncWithConcurrentDifferentRequestIdsDoesNotLoseExistingBalanceUpdates()
    {
        const int concurrentRequestCount = 100;
        const int itemId = 2002;

        var player = await CreatePlayerAsync("ExistingRacePlayer");
        var createdAt = DateTimeOffset.UtcNow;

        // 첫 행 생성 경합과 기존 행 갱신 경합을 분리하기 위해 지갑과 인벤토리를 미리 저장합니다.
        await using (var setupDbContext = _databaseFixture.CreateDbContext())
        {
            setupDbContext.PlayerWallets.Add(new PlayerWallet(player.Id, createdAt));
            setupDbContext.InventoryItems.Add(new InventoryItem(player.Id, itemId, 1, createdAt));
            await setupDbContext.SaveChangesAsync();
        }

        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rewardTasks = Enumerable.Range(0, concurrentRequestCount)
            .Select(index => WriteRewardAfterStartSignalAsync(
                new RewardWriteCommand(
                    Guid.NewGuid(),
                    player.Id,
                    1,
                    itemId,
                    1,
                    $"existing-concurrent-reward-{index}"),
                startSignal.Task))
            .ToArray();

        startSignal.SetResult(true);
        var results = await Task.WhenAll(rewardTasks);

        Assert.All(results, result =>
        {
            Assert.Equal(RewardWriteError.None, result.Error);
            Assert.NotNull(result.Receipt);
            Assert.False(result.IsReplay);
        });

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var inventoryItem = await assertionDbContext.InventoryItems.SingleAsync(
            entity => entity.PlayerId == player.Id && entity.ItemId == itemId);
        var rewardAuditCount = await assertionDbContext.RewardAudits
            .CountAsync(entity => entity.PlayerId == player.Id);

        Assert.Equal(concurrentRequestCount, wallet.Gold);
        Assert.Equal(concurrentRequestCount + 1, inventoryItem.Quantity);
        Assert.Equal(concurrentRequestCount, rewardAuditCount);
    }

    /// <summary>
    /// 같은 키를 다른 지급 내용에 사용하면 기존 보상을 바꾸지 않고 충돌로 거부하는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task WriteAsyncRejectsDifferentRewardDataForExistingRequestId()
    {
        var player = await CreatePlayerAsync("ConflictRewardPlayer");
        var originalCommand = new RewardWriteCommand(
            Guid.NewGuid(),
            player.Id,
            500,
            null,
            null,
            "original-reward");

        var originalResult = await _rewardWriter.WriteAsync(originalCommand);
        Assert.Equal(RewardWriteError.None, originalResult.Error);
        Assert.NotNull(originalResult.Receipt);
        Assert.False(originalResult.IsReplay);

        var changedCommand = originalCommand with { GoldAmount = 999 };
        var conflictResult = await _rewardWriter.WriteAsync(changedCommand);

        Assert.Equal(RewardWriteError.IdempotencyConflict, conflictResult.Error);
        Assert.False(conflictResult.IsReplay);
        Assert.Null(conflictResult.Receipt);

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var rewardAudits = await assertionDbContext.RewardAudits
            .Where(entity => entity.RequestId == originalCommand.RequestId)
            .ToListAsync();

        Assert.Equal(500, wallet.Gold);
        Assert.Single(rewardAudits);
        Assert.Equal(500, rewardAudits[0].GoldAmount);
    }

    /// <summary>
    /// 지급 사유 양끝 공백은 저장 전에 정규화되므로, 공백만 다른 재시도도 같은 요청으로 처리하는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task WriteAsyncReplaysRequestWhenOnlyReasonSurroundingWhitespaceDiffers()
    {
        var player = await CreatePlayerAsync("ReasonPlayer");
        var originalCommand = new RewardWriteCommand(
            Guid.NewGuid(),
            player.Id,
            100,
            null,
            null,
            "  normalized-reward  ");

        var firstResult = await _rewardWriter.WriteAsync(originalCommand);
        var replayResult = await _rewardWriter.WriteAsync(
            originalCommand with { Reason = "normalized-reward" });

        Assert.False(firstResult.IsReplay);
        Assert.True(replayResult.IsReplay);
        Assert.NotNull(firstResult.Receipt);
        Assert.NotNull(replayResult.Receipt);
        Assert.Equal(firstResult.Receipt.RewardAuditId, replayResult.Receipt.RewardAuditId);
        Assert.Equal("normalized-reward", replayResult.Receipt.Reason);

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        Assert.Equal(
            1,
            await assertionDbContext.RewardAudits.CountAsync(
                entity => entity.RequestId == originalCommand.RequestId));
    }

    /// <summary>
    /// 보상 이력 저장 후 지갑 갱신이 실패하면 모든 DB 변경이 롤백되는지 검증합니다.
    /// </summary>
    [Fact]
    public async Task WriteAsyncRollsBackRewardAuditWhenWalletUpdateOverflows()
    {
        var player = await CreatePlayerAsync("RollbackRewardPlayer");
        var originalWallet = new PlayerWallet(player.Id, DateTimeOffset.UtcNow);
        originalWallet.AddGold(long.MaxValue, DateTimeOffset.UtcNow);

        await using (var setupDbContext = _databaseFixture.CreateDbContext())
        {
            setupDbContext.PlayerWallets.Add(originalWallet);
            await setupDbContext.SaveChangesAsync();
        }

        var command = new RewardWriteCommand(
            Guid.NewGuid(),
            player.Id,
            1,
            null,
            null,
            "force-wallet-overflow");

        await Assert.ThrowsAsync<OverflowException>(
            () => _rewardWriter.WriteAsync(command));

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        var wallet = await assertionDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == player.Id);
        var rewardAudits = await assertionDbContext.RewardAudits
            .Where(entity => entity.RequestId == command.RequestId)
            .ToListAsync();

        // reward_audits는 지갑 변경보다 먼저 SaveChangesAsync를 했지만 같은 트랜잭션이므로 함께 취소되어야 합니다.
        Assert.Equal(long.MaxValue, wallet.Gold);
        Assert.Empty(rewardAudits);
    }

    /// <summary>존재하지 않는 Player 보상은 업무 오류로 반환하고 어떤 DB 행도 남기지 않는지 검증합니다.</summary>
    [Fact]
    public async Task WriteAsyncReturnsPlayerNotFoundWithoutCreatingRewardData()
    {
        var missingPlayerId = Guid.NewGuid();
        var command = new RewardWriteCommand(
            Guid.NewGuid(),
            missingPlayerId,
            100,
            null,
            null,
            "missing-player-test");

        var result = await _rewardWriter.WriteAsync(command);

        Assert.Equal(RewardWriteError.PlayerNotFound, result.Error);
        Assert.False(result.IsReplay);
        Assert.Null(result.Receipt);

        await using var assertionDbContext = _databaseFixture.CreateDbContext();
        Assert.False(await assertionDbContext.PlayerWallets.AnyAsync(entity => entity.PlayerId == missingPlayerId));
        Assert.False(await assertionDbContext.RewardAudits.AnyAsync(entity => entity.RequestId == command.RequestId));
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
    /// 여러 보상 작업을 미리 대기시킨 뒤 하나의 신호로 동시에 시작합니다.
    /// </summary>
    private async Task<RewardWriteResult> WriteRewardAfterStartSignalAsync(
        RewardWriteCommand command,
        Task startSignal)
    {
        await startSignal;
        return await _rewardWriter.WriteAsync(command);
    }
}
