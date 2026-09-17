using System.Diagnostics.Metrics;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Players;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Orleans.TestingHost;
using StackExchange.Redis;

namespace CoopGameServer.IntegrationTests.Grains.Players;

/// <summary>
/// 실제 Orleans TestCluster와 PostgreSQL을 사용해 PlayerGrain의 순서 처리와 영속 결과를 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class PlayerGrainTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;
    private readonly TestCluster _cluster = fixture.Cluster;

    [Fact]
    public async Task GrantAdminRewardAsyncAppliesRewardAndReturnsPostgreSqlReceipt()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);

        var result = await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(
                requestId,
                500,
                1001,
                2,
                "  player-grain-integration  "));

        Assert.Equal(PlayerRewardCommandStatus.Applied, result.Status);
        Assert.Equal(PlayerRewardCommandError.None, result.Error);
        Assert.False(result.IsReplay);

        var receipt = Assert.IsType<PlayerRewardReceipt>(result.Receipt);
        Assert.Equal(requestId, receipt.RequestId);
        Assert.Equal(playerId, receipt.PlayerId);
        Assert.Equal(500, receipt.GoldAmount);
        Assert.Equal(1001, receipt.ItemId);
        Assert.Equal(2, receipt.ItemQuantity);
        Assert.Equal("player-grain-integration", receipt.Reason);

        await using var gameDbContext = _fixture.CreateDbContext();
        var wallet = await gameDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == playerId);
        var inventoryItem = await gameDbContext.InventoryItems.SingleAsync(
            entity => entity.PlayerId == playerId && entity.ItemId == 1001);
        var rewardAudit = await gameDbContext.RewardAudits.SingleAsync(
            entity => entity.RequestId == requestId);

        Assert.Equal(500, wallet.Gold);
        Assert.Equal(2, inventoryItem.Quantity);
        Assert.Equal(receipt.RewardAuditId, rewardAudit.Id);
        Assert.Equal(receipt.Reason, rewardAudit.Reason);
    }

    [Fact]
    public async Task SameAdminRewardRequestReturnsOriginalReceiptAsReplay()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        var command = new GrantPlayerRewardCommand(
            requestId,
            300,
            1002,
            3,
            "same-admin-request");

        var firstResult = await player.GrantAdminRewardAsync(command);
        var replayResult = await player.GrantAdminRewardAsync(command);

        Assert.False(firstResult.IsReplay);
        Assert.True(replayResult.IsReplay);
        Assert.Equal(PlayerRewardCommandStatus.Applied, replayResult.Status);
        Assert.Equal(PlayerRewardCommandError.None, replayResult.Error);

        var firstReceipt = Assert.IsType<PlayerRewardReceipt>(firstResult.Receipt);
        var replayReceipt = Assert.IsType<PlayerRewardReceipt>(replayResult.Receipt);
        Assert.Equal(firstReceipt.RewardAuditId, replayReceipt.RewardAuditId);
        Assert.Equal(firstReceipt.RequestId, replayReceipt.RequestId);
        Assert.Equal(firstReceipt.PlayerId, replayReceipt.PlayerId);
        Assert.Equal(firstReceipt.GoldAmount, replayReceipt.GoldAmount);
        Assert.Equal(firstReceipt.ItemId, replayReceipt.ItemId);
        Assert.Equal(firstReceipt.ItemQuantity, replayReceipt.ItemQuantity);
        Assert.Equal(firstReceipt.Reason, replayReceipt.Reason);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.Equal(
            300,
            (await gameDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == playerId)).Gold);
        Assert.Equal(
            3,
            (await gameDbContext.InventoryItems.SingleAsync(
                entity => entity.PlayerId == playerId && entity.ItemId == 1002)).Quantity);
        Assert.Equal(
            1,
            await gameDbContext.RewardAudits.CountAsync(entity => entity.RequestId == requestId));
    }

    [Fact]
    public async Task SameRequestIdWithDifferentRewardReturnsIdempotencyConflict()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        var originalCommand = new GrantPlayerRewardCommand(
            requestId,
            400,
            null,
            null,
            "original-player-reward");

        await player.GrantAdminRewardAsync(originalCommand);
        var conflictResult = await player.GrantAdminRewardAsync(
            originalCommand with { GoldAmount = 999 });

        Assert.Equal(PlayerRewardCommandStatus.Rejected, conflictResult.Status);
        Assert.Equal(PlayerRewardCommandError.IdempotencyConflict, conflictResult.Error);
        Assert.False(conflictResult.IsReplay);
        Assert.Null(conflictResult.Receipt);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.Equal(
            400,
            (await gameDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == playerId)).Gold);
        Assert.Equal(
            1,
            await gameDbContext.RewardAudits.CountAsync(entity => entity.RequestId == requestId));
    }

    [Fact]
    public async Task UnknownPlayerReturnsPlayerNotFoundWithoutWritingRewardData()
    {
        var missingPlayerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var player = GetPlayer(missingPlayerId);

        var result = await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(
                requestId,
                100,
                null,
                null,
                "missing-player"));

        Assert.Equal(PlayerRewardCommandStatus.Rejected, result.Status);
        Assert.Equal(PlayerRewardCommandError.PlayerNotFound, result.Error);
        Assert.False(result.IsReplay);
        Assert.Null(result.Receipt);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.False(await gameDbContext.PlayerWallets.AnyAsync(entity => entity.PlayerId == missingPlayerId));
        Assert.False(await gameDbContext.RewardAudits.AnyAsync(entity => entity.RequestId == requestId));
    }

    [Fact]
    public async Task InvalidAdminRewardsAreRejectedBeforeDatabaseWrite()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        var invalidCommands = new[]
        {
            new GrantPlayerRewardCommand(Guid.Empty, 100, null, null, "empty-request-id"),
            new GrantPlayerRewardCommand(Guid.NewGuid(), -1, null, null, "negative-gold"),
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, null, null, "no-reward"),
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, 1001, null, "missing-quantity"),
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, null, 1, "missing-item-id"),
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, 0, 1, "invalid-item-id"),
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, 1001, 0, "invalid-quantity"),
            new GrantPlayerRewardCommand(Guid.NewGuid(), 100, null, null, "   "),
            new GrantPlayerRewardCommand(Guid.NewGuid(), 100, null, null, new string('x', 101)),
        };

        foreach (var command in invalidCommands)
        {
            var result = await player.GrantAdminRewardAsync(command);

            Assert.Equal(PlayerRewardCommandStatus.Rejected, result.Status);
            Assert.Equal(PlayerRewardCommandError.InvalidRequest, result.Error);
            Assert.False(result.IsReplay);
            Assert.Null(result.Receipt);
        }

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.False(await gameDbContext.PlayerWallets.AnyAsync(entity => entity.PlayerId == playerId));
        Assert.False(await gameDbContext.RewardAudits.AnyAsync(entity => entity.PlayerId == playerId));
    }

    [Fact]
    public async Task ConcurrentDifferentRequestsForSamePlayerPersistEveryReward()
    {
        const int requestCount = 25;
        const int itemId = 2001;
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);

        // 동시에 호출해도 같은 PlayerGrain 활성화로 모이며, 최종 DB에는 모든 서로 다른 요청이 남아야 합니다.
        // 이 검사는 처리 손실 방지 결과를 확인하며, 호출 완료 순서 자체를 FIFO로 단정하지는 않습니다.
        var rewardTasks = Enumerable.Range(0, requestCount)
            .Select(index => player.GrantAdminRewardAsync(
                new GrantPlayerRewardCommand(
                    Guid.NewGuid(),
                    1,
                    itemId,
                    1,
                    $"sequential-player-reward-{index}")))
            .ToArray();

        var results = await Task.WhenAll(rewardTasks);

        Assert.All(results, result =>
        {
            Assert.Equal(PlayerRewardCommandStatus.Applied, result.Status);
            Assert.Equal(PlayerRewardCommandError.None, result.Error);
            Assert.False(result.IsReplay);
            Assert.NotNull(result.Receipt);
        });

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.Equal(
            requestCount,
            (await gameDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == playerId)).Gold);
        Assert.Equal(
            requestCount,
            (await gameDbContext.InventoryItems.SingleAsync(
                entity => entity.PlayerId == playerId && entity.ItemId == itemId)).Quantity);
        Assert.Equal(
            requestCount,
            await gameDbContext.RewardAudits.CountAsync(entity => entity.PlayerId == playerId));
    }

    [Fact]
    public async Task AppliedRewardReplaySurvivesSiloRestart()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var command = new GrantPlayerRewardCommand(
            Guid.NewGuid(),
            700,
            null,
            null,
            "silo-restart-replay");

        var firstResult = await GetPlayer(playerId).GrantAdminRewardAsync(command);
        var firstReceipt = Assert.IsType<PlayerRewardReceipt>(firstResult.Receipt);

        // 모든 테스트 Silo를 재시작해 Grain 실행 객체를 버린 뒤 PostgreSQL의 멱등성 결과를 다시 읽습니다.
        await _fixture.RestartAllSilosAsync();

        var replayResult = await GetPlayer(playerId).GrantAdminRewardAsync(command);
        var replayReceipt = Assert.IsType<PlayerRewardReceipt>(replayResult.Receipt);

        Assert.True(replayResult.IsReplay);
        Assert.Equal(firstReceipt.RewardAuditId, replayReceipt.RewardAuditId);
        Assert.Equal(PlayerRewardCommandError.None, replayResult.Error);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.Equal(
            700,
            (await gameDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == playerId)).Gold);
        Assert.Equal(
            1,
            await gameDbContext.RewardAudits.CountAsync(entity => entity.RequestId == command.RequestId));
    }

    [Fact]
    public async Task GetProgressionPageAsyncReturnsGoldAndItemIdOrderedPages()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);

        // 저장 순서와 조회 순서를 다르게 만들어 ItemId 정렬과 연속 토큰을 함께 검증합니다.
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 700, 3003, 3, "third-item"));
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, 3001, 1, "first-item"));
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, 3002, 2, "second-item"));

        var firstPage = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 2, ContinuationToken: null));

        Assert.Equal(PlayerProgressionQueryError.None, firstPage.Error);
        Assert.Equal(700, firstPage.Gold);
        Assert.Equal([3001, 3002], firstPage.Items.Select(item => item.ItemId));
        Assert.NotNull(firstPage.NextContinuationToken);

        var secondPage = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 2, firstPage.NextContinuationToken));

        Assert.Equal(PlayerProgressionQueryError.None, secondPage.Error);
        Assert.Equal(700, secondPage.Gold);
        Assert.Equal([3003], secondPage.Items.Select(item => item.ItemId));
        Assert.Null(secondPage.NextContinuationToken);
    }

    [Fact]
    public async Task GetProgressionPageAsyncReturnsExplicitValidationAndMissingPlayerErrors()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);

        var invalidPageSize = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 0, ContinuationToken: null));
        var missingPlayer = await GetPlayer(Guid.NewGuid()).GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 10, ContinuationToken: null));

        Assert.Equal(PlayerProgressionQueryError.InvalidPageSize, invalidPageSize.Error);
        Assert.Empty(invalidPageSize.Items);

        // 일반 오류, '=' 패딩이 붙은 표현, 같은 바이트로 풀리지만 pad bit가 다른 비정규 표현을 모두 거부합니다.
        foreach (var invalidContinuationToken in new[] { "not-base64url", "djE6MQ==", "djE6MR" })
        {
            var invalidToken = await player.GetProgressionPageAsync(
                new GetPlayerProgressionPageQuery(PageSize: 10, invalidContinuationToken));

            Assert.Equal(PlayerProgressionQueryError.InvalidContinuationToken, invalidToken.Error);
            Assert.Empty(invalidToken.Items);
        }

        Assert.Equal(PlayerProgressionQueryError.PlayerNotFound, missingPlayer.Error);
        Assert.Empty(missingPlayer.Items);
    }

    [Fact]
    public async Task FirstPageUsesCachedSnapshotUntilExplicitInvalidation()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 100, null, null, "cache-hit"));

        var cached = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));

        await using var redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        var cachedJson = await redis.GetDatabase().HashGetAsync(
            $"coopgame:test:player-progression:v1:{playerId:N}",
            "first:20");
        Assert.False(cachedJson.IsNullOrEmpty, "첫 DB 조회 뒤 Redis에 캐시 값이 저장되어야 합니다.");

        // Redis를 거치지 않는 직접 DB 변경으로 캐시와 원본을 의도적으로 다르게 만듭니다.
        await using (var gameDbContext = _fixture.CreateDbContext())
        {
            var wallet = await gameDbContext.PlayerWallets.SingleAsync(
                entity => entity.PlayerId == playerId);
            wallet.AddGold(999, DateTimeOffset.UtcNow);
            await gameDbContext.SaveChangesAsync();
        }

        var cacheHit = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));

        Assert.Equal(100, cached.Gold);
        Assert.Equal(100, cacheHit.Gold);

        await player.InvalidateProgressionCacheAsync();
        var afterInvalidation = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));

        Assert.Equal(1099, afterInvalidation.Gold);
    }

    [Fact]
    public async Task AppliedRewardInvalidatesPreviouslyCachedProgression()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);

        var beforeReward = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 250, 4101, 2, "cache-invalidation"));
        var afterReward = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));

        Assert.Equal(0, beforeReward.Gold);
        Assert.Equal(250, afterReward.Gold);
        Assert.Contains(afterReward.Items, item => item.ItemId == 4101 && item.Quantity == 2);
    }

    [Fact]
    public async Task ExpiredFirstPageIsFilledAgainFromPostgreSql()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 300, null, null, "cache-expiration"));

        var cached = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));

        await using (var gameDbContext = _fixture.CreateDbContext())
        {
            var wallet = await gameDbContext.PlayerWallets.SingleAsync(
                entity => entity.PlayerId == playerId);
            wallet.AddGold(1, DateTimeOffset.UtcNow);
            await gameDbContext.SaveChangesAsync();
        }

        // TestCluster의 캐시 TTL 5초가 지나 Redis Hash가 자동 삭제될 때까지 기다립니다.
        await Task.Delay(TimeSpan.FromMilliseconds(5500));

        var afterExpiration = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));

        Assert.Equal(300, cached.Gold);
        Assert.Equal(301, afterExpiration.Gold);
    }

    [Fact]
    public async Task ConcurrentFirstPageMissesProduceSingleDatabaseFill()
    {
        const int requestCount = 20;
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        var databaseFillCount = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "CoopGameServer.PlayerProgressionCache" &&
                instrument.Name == "coopgame.player_progression_cache.database_fill_duration")
            {
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (_, _, _, _) => Interlocked.Increment(ref databaseFillCount));
        listener.Start();

        // 같은 PlayerGrain은 요청을 순서대로 처리합니다. 첫 요청만 DB에서 채우고,
        // 뒤에 대기한 요청은 Redis Hit가 되어 같은 키의 Cache Stampede를 만들지 않아야 합니다.
        var requests = Enumerable.Range(0, requestCount)
            .Select(_ => player.GetProgressionPageAsync(
                new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null)))
            .ToArray();

        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.Equal(PlayerProgressionQueryError.None, result.Error));
        Assert.Equal(1, Volatile.Read(ref databaseFillCount));
    }

    [Fact]
    public async Task ContinuationPageDoesNotRecordCacheDatabaseFillDuration()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, 6101, 1, "first-page-item"));
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 0, 6102, 1, "second-page-item"));

        var databaseFillCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "CoopGameServer.PlayerProgressionCache" &&
                instrument.Name == "coopgame.player_progression_cache.database_fill_duration")
            {
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (_, _, _, _) => Interlocked.Increment(ref databaseFillCount));
        listener.Start();

        var firstPage = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 1, ContinuationToken: null));
        Assert.NotNull(firstPage.NextContinuationToken);

        await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 1, firstPage.NextContinuationToken));

        Assert.Equal(1, Volatile.Read(ref databaseFillCount));
    }

    [Fact]
    public async Task CorruptFirstPageIsDiscardedAndFilledAgainFromPostgreSql()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 520, null, null, "corrupt-cache"));

        await using var redis = await ConnectionMultiplexer.ConnectAsync(_fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        var cacheKey = $"coopgame:test:player-progression:v1:{playerId:N}";
        await database.HashSetAsync(cacheKey, "first:20", "{not-valid-json");

        var result = await player.GetProgressionPageAsync(
            new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));
        var repairedJson = await database.HashGetAsync(cacheKey, "first:20");

        Assert.Equal(PlayerProgressionQueryError.None, result.Error);
        Assert.Equal(520, result.Gold);
        Assert.False(repairedJson.IsNullOrEmpty);
        Assert.NotEqual("{not-valid-json", repairedJson.ToString());
    }

    [Fact]
    public async Task RedisOutageFallsBackToPostgreSqlAndDoesNotFailProgression()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        await GetPlayer(playerId).GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(Guid.NewGuid(), 450, null, null, "redis-outage"));

        // 공유 Redis를 중지하면 다른 테스트의 연결까지 오염됩니다. 별도 TestCluster에 닫힌 포트를
        // 주입해 Redis 연결 실패를 재현하고, 같은 PostgreSQL 원본을 읽는지만 독립적으로 검증합니다.
        var clusterBuilder = new TestClusterBuilder();
        clusterBuilder.Properties[OrleansTestClusterFixture.GameDbConnectionStringKey] =
            _fixture.GameDbConnectionString;
        clusterBuilder.Properties[OrleansTestClusterFixture.RedisConnectionStringKey] =
            "127.0.0.1:1";
        clusterBuilder.AddSiloBuilderConfigurator<OrleansTestSiloConfigurator>();

        using var unavailableRedisCluster = clusterBuilder.Build();
        await unavailableRedisCluster.DeployAsync();

        try
        {
            var isolatedPlayer = unavailableRedisCluster.Client.GetGrain<IPlayerGrain>(playerId);
            var rewardCommand = new GrantPlayerRewardCommand(
                Guid.NewGuid(),
                25,
                null,
                null,
                "redis-outage-reward");
            var firstReward = await isolatedPlayer.GrantAdminRewardAsync(rewardCommand);
            var replayReward = await isolatedPlayer.GrantAdminRewardAsync(rewardCommand);
            var result = await isolatedPlayer.GetProgressionPageAsync(
                new GetPlayerProgressionPageQuery(PageSize: 20, ContinuationToken: null));

            Assert.Equal(PlayerRewardCommandStatus.Applied, firstReward.Status);
            Assert.False(firstReward.IsReplay);
            Assert.Equal(PlayerRewardCommandStatus.Applied, replayReward.Status);
            Assert.True(replayReward.IsReplay);
            Assert.Equal(firstReward.Receipt?.RewardAuditId, replayReward.Receipt?.RewardAuditId);
            Assert.Equal(PlayerProgressionQueryError.None, result.Error);
            Assert.Equal(playerId, result.PlayerId);
            Assert.NotNull(result.Nickname);
            Assert.Equal(475, result.Gold);
        }
        finally
        {
            await unavailableRedisCluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task CompleteGameVictoryAppliesVersionedServerRewardAndReplaysIt()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);
        var command = new CompletePlayerGameCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "coop-dungeon-normal-v1",
            GameOutcome.Victory,
            RewardPolicyVersion: 1);

        var firstResult = await player.CompleteGameAsync(command);
        var replayResult = await player.CompleteGameAsync(command);

        Assert.Equal(PlayerRewardCommandStatus.Applied, firstResult.Status);
        Assert.Equal(PlayerRewardCommandError.None, firstResult.Error);
        Assert.False(firstResult.IsReplay);

        var firstReceipt = Assert.IsType<PlayerRewardReceipt>(firstResult.Receipt);
        Assert.Equal(command.RequestId, firstReceipt.RequestId);
        Assert.Equal(playerId, firstReceipt.PlayerId);
        Assert.Equal(500, firstReceipt.GoldAmount);
        Assert.Equal(1001, firstReceipt.ItemId);
        Assert.Equal(1, firstReceipt.ItemQuantity);
        Assert.Contains(command.RoomId.ToString("D"), firstReceipt.Reason, StringComparison.Ordinal);
        Assert.Contains(command.QueueKey, firstReceipt.Reason, StringComparison.Ordinal);

        Assert.Equal(PlayerRewardCommandStatus.Applied, replayResult.Status);
        Assert.Equal(PlayerRewardCommandError.None, replayResult.Error);
        Assert.True(replayResult.IsReplay);
        Assert.Equal(firstReceipt.RewardAuditId, replayResult.Receipt?.RewardAuditId);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.Equal(
            500,
            (await gameDbContext.PlayerWallets.SingleAsync(entity => entity.PlayerId == playerId)).Gold);
        Assert.Equal(
            1,
            (await gameDbContext.InventoryItems.SingleAsync(
                entity => entity.PlayerId == playerId && entity.ItemId == 1001)).Quantity);
        Assert.Equal(
            1,
            await gameDbContext.RewardAudits.CountAsync(entity => entity.RequestId == command.RequestId));
    }

    [Theory]
    [InlineData(GameOutcome.Defeat)]
    [InlineData(GameOutcome.Cancelled)]
    public async Task CompleteGameNonVictoryReturnsNoRewardWithoutWritingAudit(GameOutcome outcome)
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var requestId = Guid.NewGuid();

        var result = await GetPlayer(playerId).CompleteGameAsync(
            new CompletePlayerGameCommand(
                requestId,
                Guid.NewGuid(),
                "coop-dungeon-normal-v1",
                outcome,
                RewardPolicyVersion: 1));

        Assert.Equal(PlayerRewardCommandStatus.NoReward, result.Status);
        Assert.Equal(PlayerRewardCommandError.None, result.Error);
        Assert.False(result.IsReplay);
        Assert.Null(result.Receipt);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.False(await gameDbContext.PlayerWallets.AnyAsync(entity => entity.PlayerId == playerId));
        Assert.False(await gameDbContext.InventoryItems.AnyAsync(entity => entity.PlayerId == playerId));
        Assert.False(await gameDbContext.RewardAudits.AnyAsync(entity => entity.RequestId == requestId));
    }

    [Fact]
    public async Task CompleteGameDistinguishesInvalidUnsupportedAndMissingPlayerResults()
    {
        var playerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(playerId);
        var player = GetPlayer(playerId);

        var invalidResult = await player.CompleteGameAsync(
            new CompletePlayerGameCommand(
                Guid.Empty,
                Guid.NewGuid(),
                "coop-dungeon-normal-v1",
                GameOutcome.Victory,
                RewardPolicyVersion: 1));
        var unsupportedQueueResult = await player.CompleteGameAsync(
            new CompletePlayerGameCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "unsupported-queue",
                GameOutcome.Victory,
                RewardPolicyVersion: 1));
        var unsupportedVersionResult = await player.CompleteGameAsync(
            new CompletePlayerGameCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "coop-dungeon-normal-v1",
                GameOutcome.Victory,
                RewardPolicyVersion: 999));
        var missingPlayerResult = await GetPlayer(Guid.NewGuid()).CompleteGameAsync(
            new CompletePlayerGameCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "coop-dungeon-normal-v1",
                GameOutcome.Defeat,
                RewardPolicyVersion: 1));

        Assert.Equal(PlayerRewardCommandStatus.Rejected, invalidResult.Status);
        Assert.Equal(PlayerRewardCommandError.InvalidRequest, invalidResult.Error);
        Assert.Null(invalidResult.Receipt);

        Assert.Equal(PlayerRewardCommandStatus.Rejected, unsupportedQueueResult.Status);
        Assert.Equal(PlayerRewardCommandError.UnsupportedRewardPolicy, unsupportedQueueResult.Error);
        Assert.Null(unsupportedQueueResult.Receipt);

        Assert.Equal(PlayerRewardCommandStatus.Rejected, unsupportedVersionResult.Status);
        Assert.Equal(PlayerRewardCommandError.UnsupportedRewardPolicy, unsupportedVersionResult.Error);
        Assert.Null(unsupportedVersionResult.Receipt);

        Assert.Equal(PlayerRewardCommandStatus.Rejected, missingPlayerResult.Status);
        Assert.Equal(PlayerRewardCommandError.PlayerNotFound, missingPlayerResult.Error);
        Assert.Null(missingPlayerResult.Receipt);
    }

    /// <summary>Guid Grain Key를 사용하는 PlayerGrain Proxy를 반환합니다.</summary>
    private IPlayerGrain GetPlayer(Guid playerId)
    {
        return _cluster.Client.GetGrain<IPlayerGrain>(playerId);
    }
}
