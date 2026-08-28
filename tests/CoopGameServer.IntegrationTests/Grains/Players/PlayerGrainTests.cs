using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Players;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Orleans.TestingHost;

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
