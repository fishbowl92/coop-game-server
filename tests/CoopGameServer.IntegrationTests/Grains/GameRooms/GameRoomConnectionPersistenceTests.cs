using CoopGameServer.Domain.GameRooms;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>실제 Silo 재시작과 PostgreSQL 행으로 연결 자격·세대·최초 결과의 보존을 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomConnectionPersistenceTests(OrleansTestClusterFixture fixture)
{
    [Fact]
    public async Task FailedReconnectCommitKeepsGenerationAndCompetingRequestsHaveOneWinner()
    {
        var clock = CombatTestTimeProvider.Shared;
        clock.Set(DateTimeOffset.UtcNow);
        try
        {
            var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            await fixture.RegisterPlayersAsync(ids);
            var roomId = Guid.NewGuid();
            var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
            await room.CreateAsync(Guid.NewGuid(), new MatchAssignment(roomId, "coop-dungeon-normal-v1", [], ids, clock.GetUtcNow()));
            var first = await room.ExecuteConnectionAsync(new(Guid.NewGuid(), ids[0], GameRoomConnectionAction.Connect));
            var request = new GameRoomConnectionCommand(Guid.NewGuid(), ids[0], GameRoomConnectionAction.Reconnect, Generation: 1);
            await using (var context = fixture.CreateDbContext())
            {
                // 테스트 DB에만 키 충돌을 주입해 후보 저장 전체가 실패하도록 만듭니다.
                context.GameRoomRequests.Add(new CoopGameServer.Persistence.GameRooms.GameRoomRequestRecord(
                    request.RequestId, roomId, "Start", null, "{}", clock.GetUtcNow()));
                await context.SaveChangesAsync();
            }
            Assert.NotNull(await Record.ExceptionAsync(() => room.ExecuteConnectionAsync(request)));
            Assert.Equal(1, (await room.GetPlayerViewAsync(ids[0])).Generation);
            await using (var context = fixture.CreateDbContext())
            {
                var saved = await context.GameRoomPlayers.SingleAsync(p => p.RoomId == roomId && p.PlayerId == ids[0]);
                Assert.Equal(first.ConnectionId, saved.ConnectionId);
                Assert.Equal(1, saved.ConnectionGeneration);
                await context.GameRoomRequests.Where(r => r.RoomId == roomId && r.RequestId == request.RequestId).ExecuteDeleteAsync();
            }
            var results = await Task.WhenAll(room.ExecuteConnectionAsync(request),
                room.ExecuteConnectionAsync(request with { RequestId = Guid.NewGuid() }));
            Assert.Single(results, result => result.Error == "None");
            Assert.Single(results, result => result.Error == "StaleConnection");
            Assert.Equal(2, (await room.GetPlayerViewAsync(ids[0])).Generation);
        }
        finally { clock.Reset(); }
    }

    [Fact]
    public async Task ReconnectReceiptSurvivesRestartAndHeartbeatDoesNotGrowHistory()
    {
        var clock = CombatTestTimeProvider.Shared;
        clock.Set(DateTimeOffset.UtcNow);
        try
        {
            var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            await fixture.RegisterPlayersAsync(ids);
            var roomId = Guid.NewGuid();
            var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
            await room.CreateAsync(Guid.NewGuid(), new MatchAssignment(roomId, "coop-dungeon-normal-v1", [], ids, clock.GetUtcNow()));
            var connect = new GameRoomConnectionCommand(Guid.NewGuid(), ids[0], GameRoomConnectionAction.Connect);
            var first = await room.ExecuteConnectionAsync(connect);
            Assert.Equal("None", first.Error);
            Assert.NotNull(first.ConnectionId);

            clock.Advance(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 3; i++)
                Assert.Equal("None", (await room.ExecuteConnectionAsync(new(Guid.Empty, ids[0],
                    GameRoomConnectionAction.Heartbeat, first.ConnectionId!.Value, 1))).Error);
            await using (var reader = fixture.CreateDbContext())
            {
                Assert.Equal(2, await reader.GameRoomRequests.CountAsync(r => r.RoomId == roomId));
                var row = await reader.GameRoomPlayers.SingleAsync(p => p.RoomId == roomId && p.PlayerId == ids[0]);
                Assert.Equal(clock.GetUtcNow().AddSeconds(15).ToUnixTimeMilliseconds(), row.LeaseExpiresAt!.Value.ToUnixTimeMilliseconds());
            }

            var reconnect = new GameRoomConnectionCommand(Guid.NewGuid(), ids[0], GameRoomConnectionAction.Reconnect, Generation: 1);
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => room.ExecuteConnectionAsync(reconnect)));
            Assert.Single(responses, result => !result.IsReplay);
            Assert.All(responses, result => Assert.Equal(2, result.Generation));
            await fixture.RestartAllSilosAsync();
            room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
            var replay = await room.ExecuteConnectionAsync(reconnect);
            Assert.True(replay.IsReplay);
            Assert.Equal(responses[0].ConnectionId, replay.ConnectionId);
            var stale = await room.ExecuteConnectionAsync(new(Guid.Empty, ids[0], GameRoomConnectionAction.Heartbeat, first.ConnectionId!.Value, 1));
            Assert.Equal("StaleConnection", stale.Error);
            Assert.Null(stale.ConnectionId);
            Assert.Equal("SupersededReconnectRequest", (await room.ExecuteConnectionAsync(connect)).Error);

            var disconnected = await room.ExecuteConnectionAsync(new(Guid.NewGuid(), ids[0],
                GameRoomConnectionAction.Disconnect, replay.ConnectionId!.Value, 2));
            Assert.Equal("None", disconnected.Error);
            await using var context = fixture.CreateDbContext();
            var saved = await context.GameRoomPlayers.SingleAsync(p => p.RoomId == roomId && p.PlayerId == ids[0]);
            Assert.Equal(RoomConnectionStatus.Disconnected, saved.ConnectionStatus);
            Assert.Null(saved.ConnectionId);
            Assert.Equal(2, saved.ConnectionGeneration);
        }
        finally { clock.Reset(); }
    }
}
