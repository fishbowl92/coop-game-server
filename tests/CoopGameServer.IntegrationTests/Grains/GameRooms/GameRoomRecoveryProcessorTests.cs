using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.Grains.GameRooms;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>
/// Silo 재시작 뒤 게임 결과 복구 처리기가 미완료 방만 안전하게 다시 전달하는지 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomRecoveryProcessorTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;

    [Fact]
    public async Task RecoverDueRoomsAsyncResumesAfterRestartWithoutDuplicateReward()
    {
        var assignment = CreateAssignment();
        await _fixture.RegisterPlayersAsync(assignment.PlayerIds);
        await CompleteVictoryAsync(assignment);

        var responseLostPlayerId = assignment.PlayerIds[0];
        var retryDueAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using (var gameDbContext = _fixture.CreateDbContext())
        {
            // 실제 보상 Transaction은 Commit됐지만 GameRoom이 응답을 받지 못한 장애를 재현합니다.
            // RewardAudit은 그대로 두고 전달 상태만 재시도 대기로 되돌립니다.
            await gameDbContext.GameResults
                .Where(result => result.RoomId == assignment.RoomId
                    && result.PlayerId == responseLostPlayerId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(result => result.DeliveryStatus, GameResultDeliveryStatus.PendingRetry)
                    .SetProperty(result => result.NextAttemptAt, retryDueAt)
                    .SetProperty(result => result.LastErrorCode, "ResponseLostSimulation"));
        }

        // 모든 Grain 활성화를 제거하여 실제 Silo 재시작 뒤 DB에서 방을 복원하는 경로를 사용합니다.
        await _fixture.RestartAllSilosAsync();
        var processor = CreateProcessor();

        var batchResult = await processor.RecoverDueRoomsAsync(batchSize: 100);

        Assert.Equal(1, batchResult.DiscoveredRoomCount);
        Assert.Equal(1, batchResult.SucceededRoomCount);
        Assert.Equal(0, batchResult.FailedRoomCount);

        await using var verificationContext = _fixture.CreateDbContext();
        var recoveredResult = await verificationContext.GameResults.SingleAsync(
            result => result.RoomId == assignment.RoomId
                && result.PlayerId == responseLostPlayerId);

        Assert.Equal(GameResultDeliveryStatus.Applied, recoveredResult.DeliveryStatus);
        Assert.Equal(2, recoveredResult.AttemptCount);
        Assert.Null(recoveredResult.NextAttemptAt);
        Assert.Null(recoveredResult.LastErrorCode);

        // PlayerGrain은 기존 reward_audits 결과를 재생하므로 네 명의 보상 행은 여전히 네 개뿐입니다.
        Assert.Equal(
            4,
            await verificationContext.RewardAudits.CountAsync(
                audit => assignment.PlayerIds.Contains(audit.PlayerId)));
    }

    [Fact]
    public async Task RecoverDueRoomsAsyncSkipsPendingRetryBeforeNextAttemptAt()
    {
        var assignment = CreateAssignment();
        await _fixture.RegisterPlayersAsync(assignment.PlayerIds);
        await CompleteVictoryAsync(assignment);

        var waitingPlayerId = assignment.PlayerIds[0];
        var futureAttemptAt = DateTimeOffset.UtcNow.AddMinutes(10);

        await using (var gameDbContext = _fixture.CreateDbContext())
        {
            await gameDbContext.GameResults
                .Where(result => result.RoomId == assignment.RoomId
                    && result.PlayerId == waitingPlayerId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(result => result.DeliveryStatus, GameResultDeliveryStatus.PendingRetry)
                    .SetProperty(result => result.NextAttemptAt, futureAttemptAt)
                    .SetProperty(result => result.LastErrorCode, "FutureRetrySimulation"));
        }

        var processor = CreateProcessor();
        var batchResult = await processor.RecoverDueRoomsAsync(batchSize: 100);

        Assert.Equal(0, batchResult.DiscoveredRoomCount);
        Assert.Equal(0, batchResult.SucceededRoomCount);
        Assert.Equal(0, batchResult.FailedRoomCount);

        await using var verificationContext = _fixture.CreateDbContext();
        var waitingResult = await verificationContext.GameResults.SingleAsync(
            result => result.RoomId == assignment.RoomId
                && result.PlayerId == waitingPlayerId);

        Assert.Equal(GameResultDeliveryStatus.PendingRetry, waitingResult.DeliveryStatus);
        Assert.Equal(1, waitingResult.AttemptCount);
        // PostgreSQL timestamptz는 마이크로초 정밀도이므로 .NET의 100나노초 Tick과 1마이크로초 이내 차이가 날 수 있습니다.
        Assert.NotNull(waitingResult.NextAttemptAt);
        Assert.Equal(
            futureAttemptAt,
            waitingResult.NextAttemptAt.Value,
            TimeSpan.FromMicroseconds(1));
        Assert.Equal("FutureRetrySimulation", waitingResult.LastErrorCode);
    }

    [Fact]
    public async Task RecoverDueRoomsAsyncIncludesPendingResult()
    {
        var assignment = CreateAssignment();
        await _fixture.RegisterPlayersAsync(assignment.PlayerIds);
        await CompleteVictoryAsync(assignment);

        var pendingPlayerId = assignment.PlayerIds[0];

        await using (var gameDbContext = _fixture.CreateDbContext())
        {
            // 방 완료 Transaction 직후 첫 PlayerGrain 호출 전에 Silo가 중단된 상태를 재현합니다.
            // CompleteAsync가 즉시 지급한 테스트 데이터만 제거해 실제 미지급 Pending 상태로 되돌립니다.
            await gameDbContext.RewardAudits
                .Where(audit => audit.PlayerId == pendingPlayerId)
                .ExecuteDeleteAsync();
            await gameDbContext.InventoryItems
                .Where(item => item.PlayerId == pendingPlayerId)
                .ExecuteDeleteAsync();
            await gameDbContext.PlayerWallets
                .Where(wallet => wallet.PlayerId == pendingPlayerId)
                .ExecuteDeleteAsync();

            await gameDbContext.GameResults
                .Where(result => result.RoomId == assignment.RoomId
                    && result.PlayerId == pendingPlayerId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(result => result.DeliveryStatus, GameResultDeliveryStatus.Pending)
                    .SetProperty(result => result.AttemptCount, 0)
                    .SetProperty(result => result.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(result => result.LastErrorCode, (string?)null));
        }

        var processor = CreateProcessor();
        var batchResult = await processor.RecoverDueRoomsAsync(batchSize: 100);

        Assert.Equal(1, batchResult.DiscoveredRoomCount);
        Assert.Equal(1, batchResult.SucceededRoomCount);
        Assert.Equal(0, batchResult.FailedRoomCount);

        await using var verificationContext = _fixture.CreateDbContext();
        var recoveredResult = await verificationContext.GameResults.SingleAsync(
            result => result.RoomId == assignment.RoomId
                && result.PlayerId == pendingPlayerId);

        Assert.Equal(GameResultDeliveryStatus.Applied, recoveredResult.DeliveryStatus);
        Assert.Equal(1, recoveredResult.AttemptCount);
        Assert.Equal(
            4,
            await verificationContext.RewardAudits.CountAsync(
                audit => assignment.PlayerIds.Contains(audit.PlayerId)));
        Assert.Equal(
            500,
            (await verificationContext.PlayerWallets.SingleAsync(
                wallet => wallet.PlayerId == pendingPlayerId)).Gold);
        Assert.Equal(
            1,
            (await verificationContext.InventoryItems.SingleAsync(
                item => item.PlayerId == pendingPlayerId && item.ItemId == 1001)).Quantity);
    }

    /// <summary>테스트 DB와 Orleans Client를 사용하는 한 주기 복구 처리기를 만듭니다.</summary>
    private GameRoomRecoveryProcessor CreateProcessor()
    {
        return new GameRoomRecoveryProcessor(
            new FixtureDbContextFactory(_fixture),
            _fixture.Cluster.GrainFactory,
            TimeProvider.System,
            NullLogger<GameRoomRecoveryProcessor>.Instance);
    }

    /// <summary>네 플레이어가 있는 방을 만들고 시작한 뒤 승리 상태로 완료합니다.</summary>
    private async Task CompleteVictoryAsync(MatchAssignment assignment)
    {
        var gameRoom = _fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(assignment.RoomId);
        var createResult = await gameRoom.CreateAsync(Guid.NewGuid(), assignment);
        var startResult = await gameRoom.StartAsync(Guid.NewGuid());
        var completeResult = await gameRoom.CompleteAsync(Guid.NewGuid(), GameOutcome.Victory);

        Assert.Equal(GameRoomCommandError.None, createResult.Error);
        Assert.Equal(GameRoomCommandError.None, startResult.Error);
        Assert.Equal(GameRoomCommandError.None, completeResult.Error);
    }

    /// <summary>파티가 없는 네 명의 고유한 매칭 결과를 만듭니다.</summary>
    private static MatchAssignment CreateAssignment()
    {
        return new MatchAssignment(
            Guid.NewGuid(),
            "coop-dungeon-normal-v1",
            [],
            Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 테스트 Fixture의 실제 PostgreSQL Context 생성을 제품 코드가 요구하는 Factory 계약으로 연결합니다.
    /// </summary>
    private sealed class FixtureDbContextFactory(OrleansTestClusterFixture fixture)
        : IDbContextFactory<GameDbContext>
    {
        /// <inheritdoc />
        public GameDbContext CreateDbContext()
        {
            return fixture.CreateDbContext();
        }
    }
}
