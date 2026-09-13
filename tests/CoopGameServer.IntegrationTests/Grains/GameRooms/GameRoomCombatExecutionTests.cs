using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.Grains.GameRooms;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>실제 Orleans 호출·PostgreSQL 저장·재시작을 통과하는 전투 검증입니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomCombatExecutionTests(OrleansTestClusterFixture fixture)
{
    private readonly Dictionary<Guid, Guid> _connections = [];
    [Fact]
    public async Task ConcurrentReplayAndRestartKeepDamageCooldownAndSequence()
    {
        var clock = CombatTestTimeProvider.Shared;
        clock.Set(DateTimeOffset.UtcNow);
        try
        {
            var (room, assignment) = await CreateStartedRoom();
            var command = new GameRoomCombatCommand(Guid.NewGuid(), assignment.PlayerIds[0], 1, CombatActionKind.BasicAttack,
                ConnectionId: _connections[assignment.PlayerIds[0]], ConnectionGeneration: 1);
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => room.ExecuteCombatAsync(command)));
            Assert.Single(results, r => !r.IsReplay);
            Assert.All(results, r => Assert.Equal(GameRoomCombatError.None, r.Error));
            await fixture.RestartAllSilosAsync();
            room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(assignment.RoomId);
            var snapshot = await room.GetAsync();
            Assert.Equal(80, snapshot!.EnemyCurrentHealth);
            Assert.Equal(1, snapshot.Players![0].LastAcceptedCommandSequence);
            Assert.True((await room.ExecuteCombatAsync(command)).IsReplay);
            var next = command with { RequestId = Guid.NewGuid(), Sequence = 2 };
            Assert.Equal(GameRoomCombatError.CooldownActive, (await room.ExecuteCombatAsync(next)).Error);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(GameRoomCombatError.CooldownActive, (await room.ExecuteCombatAsync(next)).Error);
            Assert.Equal(GameRoomCombatError.None, (await room.ExecuteCombatAsync(next with { RequestId = Guid.NewGuid() })).Error);
        }
        finally { clock.Reset(); }
    }

    [Fact]
    public async Task FailedRequestInsertRollsBackHealthAndAllowsSameKeyRetry()
    {
        var (room, assignment) = await CreateStartedRoom();
        var command = new GameRoomCombatCommand(Guid.NewGuid(), assignment.PlayerIds[0], 1, CombatActionKind.BasicAttack,
            ConnectionId: _connections[assignment.PlayerIds[0]], ConnectionGeneration: 1);
        await using (var context = fixture.CreateDbContext())
        {
            // 일회용 DB에만 장애 행을 주입합니다. Grain 메모리에 없는 중복 기본 키로 저장 실패를 유발합니다.
            context.GameRoomRequests.Add(new GameRoomRequestRecord(command.RequestId, assignment.RoomId, "Start", null, "{}", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }
        Assert.NotNull(await Record.ExceptionAsync(() => room.ExecuteCombatAsync(command)));
        Assert.Equal(100, (await room.GetAsync())!.EnemyCurrentHealth);
        await using (var context = fixture.CreateDbContext())
        {
            Assert.Equal(100, (await context.GameRooms.SingleAsync(r => r.RoomId == assignment.RoomId)).EnemyCurrentHealth);
            Assert.All(await context.GameRoomPlayers.Where(p => p.RoomId == assignment.RoomId).ToArrayAsync(), p => Assert.Equal(0, p.LastCommandSequence));
            await context.GameRoomRequests.Where(r => r.RoomId == assignment.RoomId && r.RequestId == command.RequestId).ExecuteDeleteAsync();
        }
        Assert.Equal(GameRoomCombatError.None, (await room.ExecuteCombatAsync(command)).Error);
    }

    [Fact]
    public async Task ThreeWavesCompleteAndRewardOnceAfterRestart()
    {
        var clock = CombatTestTimeProvider.Shared;
        clock.Set(DateTimeOffset.UtcNow);
        try
        {
            var (room, assignment) = await CreateStartedRoom(withParty: true);
            var sequences = new long[4];
            GameRoomCombatCommand? finalCommand = null;
            GameRoomCombatResult? result = null;
            // 100/180/300 체력에 스킬 50으로 각 2/4/6회, 총 12회입니다.
            for (var i = 0; i < 12; i++)
            {
                var index = i % 4;
                foreach (var id in assignment.PlayerIds)
                    Assert.Equal("None", (await room.ExecuteConnectionAsync(new(Guid.Empty, id,
                        GameRoomConnectionAction.Heartbeat, _connections[id], 1))).Error);
                finalCommand = new(Guid.NewGuid(), assignment.PlayerIds[index], ++sequences[index], CombatActionKind.UseSkill,
                    ConnectionId: _connections[assignment.PlayerIds[index]], ConnectionGeneration: 1);
                result = await room.ExecuteCombatAsync(finalCommand);
                Assert.Equal(GameRoomCombatError.None, result.Error);
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(GameOutcome.Victory, result!.Room!.Outcome);
            Assert.Equal(3, result.Room.CurrentWave);
            Assert.Equal(0, result.Room.EnemyCurrentHealth);
            await fixture.RestartAllSilosAsync();
            room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(assignment.RoomId);
            Assert.True((await room.ExecuteCombatAsync(finalCommand!)).IsReplay);
            await using var context = fixture.CreateDbContext();
            Assert.Equal(4, await context.RewardAudits.CountAsync(a => assignment.PlayerIds.Contains(a.PlayerId)));
            Assert.All(await context.GameResults.Where(r => r.RoomId == assignment.RoomId).ToArrayAsync(), r => Assert.Equal(GameResultDeliveryStatus.Applied, r.DeliveryStatus));
            Assert.False((await context.GameRooms.SingleAsync(r => r.RoomId == assignment.RoomId)).FinalizationPending);
            var party = fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(assignment.PartyIds[0]);
            var partySnapshot = await party.GetAsync();
            Assert.Equal(PartyLifecycle.Active, partySnapshot!.Lifecycle);
            Assert.Equal(assignment.PlayerIds[..3], partySnapshot.MemberPlayerIds);

            // 외부 후처리는 성공했지만 마지막 DB 응답만 유실된 상황을 재현합니다.
            // 파티가 다시 대기 중이어도 이전 하위 요청을 재생해 새 상태를 되돌리지 않아야 합니다.
            await party.QueueForMatchAsync(Guid.NewGuid(), assignment.PlayerIds[0]);
            await context.GameRooms.Where(r => r.RoomId == assignment.RoomId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.FinalizationPending, true));
            await fixture.RestartAllSilosAsync();
            room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(assignment.RoomId);
            var recovery = new GameRoomRecoveryProcessor(new ContextFactory(fixture), fixture.Cluster.GrainFactory,
                clock, NullLogger<GameRoomRecoveryProcessor>.Instance);
            var recovered = await recovery.RecoverDueRoomsAsync(100);
            // 복구 범위에 만료 방도 포함되므로 공유 테스트 DB 전체 개수 대신 대상 방의 수렴을 검사합니다.
            Assert.True(recovered.DiscoveredRoomCount >= 1);
            Assert.True(recovered.SucceededRoomCount >= 1);
            Assert.False(await context.GameRooms.Where(r => r.RoomId == assignment.RoomId).Select(r => r.FinalizationPending).SingleAsync());
            Assert.Equal(PartyLifecycle.MatchQueued, (await party.GetAsync())!.Lifecycle);
            Assert.Equal(4, await context.RewardAudits.CountAsync(a => assignment.PlayerIds.Contains(a.PlayerId)));
        }
        finally { clock.Reset(); }
    }

    private async Task<(IGameRoomGrain Room, MatchAssignment Assignment)> CreateStartedRoom(bool withParty = false)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await fixture.RegisterPlayersAsync(ids);
        // 보상 정책이 지원하는 실제 모드 키를 사용합니다. 임의 키는 정상적으로 UnsupportedQueueKey가 됩니다.
        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>("coop-dungeon-normal-v1");
        MatchQueueCommandResult? queued = null;
        if (withParty)
        {
            var partyId = Guid.NewGuid();
            var party = fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(partyId);
            await party.CreateAsync(Guid.NewGuid(), ids[0]);
            await party.JoinAsync(Guid.NewGuid(), ids[1]);
            await party.JoinAsync(Guid.NewGuid(), ids[2]);
            await party.QueueForMatchAsync(Guid.NewGuid(), ids[0]);
            await queue.EnqueueAsync(new MatchQueueEntryRequest(Guid.NewGuid(), MatchQueueEntryKind.PreformedParty, partyId, ids[0], ids[..3]));
            queued = await queue.EnqueueAsync(new MatchQueueEntryRequest(Guid.NewGuid(), MatchQueueEntryKind.SoloPlayer, null, ids[3], [ids[3]]));
        }
        else
        {
            foreach (var id in ids)
            {
                queued = await queue.EnqueueAsync(new MatchQueueEntryRequest(Guid.NewGuid(), MatchQueueEntryKind.SoloPlayer, null, id, [id]));
            }
        }
        var assignment = Assert.IsType<MatchAssignment>(queued!.Match);
        var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(assignment.RoomId);
        foreach (var id in ids)
        {
            var connected = await room.ExecuteConnectionAsync(new(Guid.NewGuid(), id, GameRoomConnectionAction.Connect));
            Assert.Equal("None", connected.Error);
            _connections[id] = connected.ConnectionId!.Value;
        }
        Assert.Equal(GameRoomCommandError.None, (await room.StartAsync(Guid.NewGuid())).Error);
        return (room, assignment);
    }

    private sealed class ContextFactory(OrleansTestClusterFixture fixture) : IDbContextFactory<GameDbContext>
    {
        public GameDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
