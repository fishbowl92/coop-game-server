using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>만료에 따른 방 완료·파티 복귀·결과 저장이 실제 DB에서 함께 수렴하는지 검사합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomDeadlineIntegrationTests(OrleansTestClusterFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpirationCompletesRoomAndPreservesPreformedParty(bool start)
    {
        var clock = CombatTestTimeProvider.Shared;
        clock.Set(DateTimeOffset.UtcNow);
        try
        {
            var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            await fixture.RegisterPlayersAsync(ids);
            var partyId = Guid.NewGuid();
            var party = fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(partyId);
            await party.CreateAsync(Guid.NewGuid(), ids[0]);
            await party.JoinAsync(Guid.NewGuid(), ids[1]);
            await party.JoinAsync(Guid.NewGuid(), ids[2]);
            await party.QueueForMatchAsync(Guid.NewGuid(), ids[0]);
            var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>("coop-dungeon-normal-v1");
            await queue.EnqueueAsync(new(Guid.NewGuid(), MatchQueueEntryKind.PreformedParty, partyId, ids[0], ids[..3]));
            var match = (await queue.EnqueueAsync(new(Guid.NewGuid(), MatchQueueEntryKind.SoloPlayer, null, ids[3], [ids[3]]))).Match!;
            var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(match.RoomId);
            if (start)
            {
                GameRoomConnectionResult? first = null;
                foreach (var id in ids)
                {
                    var result = await room.ExecuteConnectionAsync(new(Guid.NewGuid(), id, GameRoomConnectionAction.Connect));
                    Assert.Equal("None", result.Error);
                    first ??= result;
                }
                Assert.Equal("None", (await room.ExecuteConnectionAsync(new(Guid.NewGuid(), ids[0],
                    GameRoomConnectionAction.StartCombat, first!.ConnectionId!.Value, 1))).Error);
            }
            clock.Advance(TimeSpan.FromSeconds(46));
            await fixture.RestartAllSilosAsync();
            room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(match.RoomId);
            await room.ReconcileDeadlinesAsync();
            var snapshot = (await room.GetAsync())!;
            Assert.Equal(start ? GameOutcome.Defeat : GameOutcome.Cancelled, snapshot.Outcome);
            if (!start) Assert.Null(snapshot.StartedAt);
            Assert.Equal(PartyLifecycle.Active, (await party.GetAsync())!.Lifecycle);
            Assert.Equal(ids[..3], (await party.GetAsync())!.MemberPlayerIds);
            await using var context = fixture.CreateDbContext();
            Assert.False(await context.GameRooms.Where(r => r.RoomId == match.RoomId).Select(r => r.FinalizationPending).SingleAsync());
            Assert.Equal(4, await context.GameResults.CountAsync(r => r.RoomId == match.RoomId));
            Assert.Equal(0, await context.RewardAudits.CountAsync(r => ids.Contains(r.PlayerId)));
            var version = snapshot.StateVersion;
            await room.ReconcileDeadlinesAsync();
            Assert.Equal(version, (await room.GetAsync())!.StateVersion);
            Assert.All(await context.GameRoomPlayers.Where(p => p.RoomId == match.RoomId).ToArrayAsync(), p => Assert.Null(p.ConnectionId));
        }
        finally { clock.Reset(); }
    }
}
