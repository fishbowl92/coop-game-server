using CoopGameServer.Api.Application.Matchmaking;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.GrainContracts.Persistence;
using CoopGameServer.Grains.GameRooms;
using CoopGameServer.Grains.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CoopGameServer.IntegrationTests.Grains.Matchmaking;

/// <summary>별도 PostgreSQL의 실제 저장 경계에 장애를 주입해 호출자 없는 복구와 최초 결과 재생을 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class DurableRecoveryTests(OrleansTestClusterFixture fixture) : IDisposable
{
    private readonly CombatTestTimeProvider _clock = FreezeClock();

    [Fact]
    public async Task CompletionPersistenceFailureKeepsPartyInGameUntilRoomCommit()
    {
        var (request, party) = await PartyRequestAsync();
        var extraPlayers = await PlayersAsync(2);
        foreach (var player in extraPlayers) await party.JoinAsync(Guid.NewGuid(), player);
        var matched = await fixture.Cluster.GrainFactory.GetGrain<IMatchmakingOperationGrain>(request.GetGrainKey()).ExecuteAsync(request);
        var roomId = matched.QueueResult!.Match!.RoomId;
        var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
        await room.StartAsync(Guid.NewGuid());
        var completionId = Guid.NewGuid();
        await using (await FaultAsync("game_rooms", "UPDATE", $"NEW.room_id = '{roomId}' AND NEW.lifecycle = 2", "40001"))
        {
            await Assert.ThrowsAsync<GrainPersistenceException>(() => room.CompleteAsync(completionId, GameOutcome.Cancelled));
            Assert.Equal(PartyLifecycle.InGame, (await party.GetAsync())!.Lifecycle);
            await using var context = fixture.CreateDbContext();
            Assert.Equal((int)GameRoomLifecycle.InGame, (await context.GameRooms.SingleAsync(row => row.RoomId == roomId)).Lifecycle);
            Assert.False(await context.GameResults.AnyAsync(row => row.RoomId == roomId));
        }
        Assert.Equal(GameRoomCommandError.None, (await room.CompleteAsync(completionId, GameOutcome.Cancelled)).Error);
        Assert.Equal(PartyLifecycle.Active, (await party.GetAsync())!.Lifecycle);
    }

    [Fact]
    public async Task CancelledRoomReleasesTicketsAfterFailureAndSiloRestart()
    {
        var queueKey = NewQueue();
        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>(queueKey);
        var players = await PlayersAsync();
        MatchQueueCommandResult? matched = null;
        foreach (var player in players) matched = await queue.EnqueueAsync(Solo(player));
        var roomId = matched!.Match!.RoomId;
        var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
        await room.StartAsync(Guid.NewGuid());
        var completionId = Guid.NewGuid();
        await using (await FaultAsync("match_queue_tickets", "DELETE", $"OLD.queue_key = '{queueKey}'"))
        {
            await Assert.ThrowsAsync<GrainPersistenceException>(() => room.CompleteAsync(completionId, GameOutcome.Cancelled));
            await using var context = fixture.CreateDbContext();
            var stored = await context.GameRooms.SingleAsync(row => row.RoomId == roomId);
            Assert.Equal((int)GameRoomLifecycle.Completed, stored.Lifecycle);
            Assert.True(stored.FinalizationPending);
            Assert.Equal(MatchQueueTicketStatus.Matched, (await queue.GetTicketAsync(matched.Ticket!.TicketId))!.Status);
        }
        await fixture.RestartAllSilosAsync();
        await RoomRecovery().RecoverDueRoomsAsync(1000);
        Assert.Equal(MatchQueueTicketStatus.Completed, (await queue.GetTicketAsync(matched.Ticket!.TicketId))!.Status);
        Assert.True((await room.CompleteAsync(completionId, GameOutcome.Cancelled)).IsReplay);
        await using var verified = fixture.CreateDbContext();
        Assert.False((await verified.GameRooms.SingleAsync(row => row.RoomId == roomId)).FinalizationPending);
    }

    [Fact]
    public async Task CommittedAssignmentCreatesMissingRoomWithoutOriginalEnqueueRetry()
    {
        var queueKey = NewQueue();
        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>(queueKey);
        var players = await PlayersAsync();
        var first = await queue.EnqueueAsync(Solo(players[0]));
        await queue.EnqueueAsync(Solo(players[1]));
        await queue.EnqueueAsync(Solo(players[2]));
        await using (await FaultAsync("game_rooms", "INSERT", $"NEW.queue_key = '{queueKey}'"))
            await Assert.ThrowsAsync<GrainPersistenceException>(() => queue.EnqueueAsync(Solo(players[3])));

        var roomId = (await queue.GetTicketAsync(first.Ticket!.TicketId))!.RoomId!.Value;
        await using (var context = fixture.CreateDbContext()) Assert.False(await context.GameRooms.AnyAsync(row => row.RoomId == roomId));
        await fixture.RestartAllSilosAsync();
        await RoomRecovery().RecoverDueRoomsAsync(1000);
        var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
        Assert.Equal(players, (await room.GetAsync())!.PlayerIds);
        await room.StartAsync(Guid.NewGuid());
        await room.CompleteAsync(Guid.NewGuid(), GameOutcome.Cancelled);
    }

    [Fact]
    public async Task PartyEnqueueIntentSurvivesQueueFailureAndRejectsChangedReplay()
    {
        var (request, party) = await PartyRequestAsync();
        var operation = fixture.Cluster.GrainFactory.GetGrain<IMatchmakingOperationGrain>(request.GetGrainKey());
        await using (await FaultAsync("match_queue_requests", "INSERT", $"NEW.queue_key = '{request.QueueKey}'", "40001"))
        {
            var error = await Assert.ThrowsAsync<GrainPersistenceException>(() => operation.ExecuteAsync(request));
            Assert.True(error.IsTransient);
            Assert.Equal("40001", error.ErrorCode);
            Assert.Null(error.InnerException);
            Assert.Equal(PartyLifecycle.MatchQueued, (await party.GetAsync())!.Lifecycle);
            await using var context = fixture.CreateDbContext();
            Assert.False(await context.MatchQueueTickets.AnyAsync(row => row.PartyId == request.TargetId));
            Assert.Null((await context.MatchmakingOperations.FindAsync(request.GetGrainKey()))!.ResultPayloadJson);
        }

        await fixture.RestartAllSilosAsync();
        _clock.Advance(TimeSpan.FromSeconds(6));
        await MatchmakingRecovery().RecoverPendingOperationsAsync(1000);
        var replay = await operation.ExecuteAsync(request);
        Assert.Equal(MatchQueueCommandError.None, replay.QueueResult!.Error);
        Assert.True(replay.QueueResult.IsReplay);
        var conflict = await operation.ExecuteAsync(request with { RequesterPlayerId = Guid.NewGuid() });
        Assert.Equal(MatchQueueCommandError.RequestIdConflict, conflict.QueueResult!.Error);
        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>(request.QueueKey);
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
        await CancelAsync(request, replay.QueueResult.Ticket!.TicketId);
    }

    [Fact]
    public async Task PartyCancellationResumesAfterTicketWasAlreadyCancelled()
    {
        var (request, party) = await PartyRequestAsync();
        var enqueued = await fixture.Cluster.GrainFactory.GetGrain<IMatchmakingOperationGrain>(request.GetGrainKey()).ExecuteAsync(request);
        var cancellation = request with { RequestId = Guid.NewGuid(), Kind = MatchmakingOperationKind.Cancel, TargetId = enqueued.QueueResult!.Ticket!.TicketId };
        var operation = fixture.Cluster.GrainFactory.GetGrain<IMatchmakingOperationGrain>(cancellation.GetGrainKey());
        await using (await FaultAsync("party_requests", "INSERT", $"NEW.party_id = '{request.TargetId}' AND NEW.command_kind = 'CancelMatchQueue'"))
        {
            await Assert.ThrowsAsync<GrainPersistenceException>(() => operation.ExecuteAsync(cancellation));
            var ticket = await fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>(request.QueueKey).GetTicketAsync(cancellation.TargetId);
            Assert.Equal(MatchQueueTicketStatus.Cancelled, ticket!.Status);
            Assert.Equal(PartyLifecycle.MatchQueued, (await party.GetAsync())!.Lifecycle);
        }
        await fixture.RestartAllSilosAsync();
        _clock.Advance(TimeSpan.FromSeconds(6));
        await MatchmakingRecovery().RecoverPendingOperationsAsync(1000);
        Assert.Equal(PartyLifecycle.Active, (await party.GetAsync())!.Lifecycle);
        Assert.True((await operation.ExecuteAsync(cancellation)).QueueResult!.IsReplay);
    }

    [Theory]
    [InlineData("40001", true)]
    [InlineData("23514", false)]
    public async Task RewardDatabaseErrorPreservesClassificationAndDoesNotBlockOtherPlayers(string sqlState, bool transient)
    {
        var players = await PlayersAsync();
        var roomId = Guid.NewGuid();
        var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
        await room.CreateAsync(Guid.NewGuid(), new(roomId, "coop-dungeon-normal-v1", [], players, _clock.GetUtcNow()));
        await room.StartAsync(Guid.NewGuid());
        var completionId = Guid.NewGuid();
        await using (await FaultAsync("reward_audits", "INSERT", $"NEW.player_id = '{players[0]}'", sqlState))
        {
            Assert.Equal(GameRoomCommandError.None, (await room.CompleteAsync(completionId, GameOutcome.Victory)).Error);
            await using var context = fixture.CreateDbContext();
            var results = await context.GameResults.Where(row => row.RoomId == roomId).ToArrayAsync();
            Assert.Equal(3, results.Count(row => row.DeliveryStatus == GameResultDeliveryStatus.Applied));
            var failed = Assert.Single(results, row => row.PlayerId == players[0]);
            Assert.Equal(1, failed.AttemptCount);
            Assert.Equal(transient ? GameResultDeliveryStatus.PendingRetry : GameResultDeliveryStatus.TerminalFailure, failed.DeliveryStatus);
            Assert.Equal(transient, failed.NextAttemptAt.HasValue);
            Assert.Equal($"Database.{sqlState}", failed.LastErrorCode);
        }
        if (transient)
        {
            _clock.Advance(TimeSpan.FromSeconds(6));
            await RoomRecovery().RecoverDueRoomsAsync(1000);
            Assert.True((await room.CompleteAsync(completionId, GameOutcome.Victory)).IsReplay);
        }
        await using var verified = fixture.CreateDbContext();
        Assert.Equal(transient ? 4 : 3, await verified.RewardAudits.CountAsync(row => players.Contains(row.PlayerId)));
    }

    [Fact]
    public async Task CancellingHttpWaitAfterIntentCommitDoesNotAbandonPartyEnqueue()
    {
        var (request, _) = await PartyRequestAsync("coop-dungeon-normal-v1");
        const long gate = 61001234;
        await using var connection = new NpgsqlConnection(fixture.GameDbConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand($"SELECT pg_advisory_lock({gate})", connection)) await command.ExecuteNonQueryAsync();
        await using var fault = await CreateTriggerAsync("party_requests", "INSERT", $"NEW.party_id = '{request.TargetId}'",
            $"PERFORM pg_advisory_xact_lock({gate}); RETURN NEW;");
        using var cancellation = new CancellationTokenSource();
        await using var context = fixture.CreateDbContext();
        var service = new MatchmakingService(fixture.Cluster.GrainFactory);
        var pending = service.EnqueuePartyAsync(request.QueueKey, request.TargetId, request.RequestId,
            request.RequesterPlayerId, false, cancellation.Token);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var read = fixture.CreateDbContext();
                return await read.MatchmakingOperations.AnyAsync(row => row.OperationKey == request.GetGrainKey());
            });
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({gate})", connection);
            await command.ExecuteNonQueryAsync();
        }
        await WaitUntilAsync(async () =>
        {
            await using var read = fixture.CreateDbContext();
            return await read.MatchmakingOperations.AnyAsync(row => row.OperationKey == request.GetGrainKey() && row.ResultPayloadJson != null);
        });
        var ticket = Assert.Single((await fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>(request.QueueKey).GetSnapshotAsync())
            .QueuedTickets, ticket => ticket.PartyId == request.TargetId);
        await CancelAsync(request, ticket.TicketId);
    }

    private async Task<(MatchmakingOperationRequest Request, IPartyGrain Party)> PartyRequestAsync(string? queueKey = null)
    {
        var players = await PlayersAsync(2);
        var partyId = Guid.NewGuid();
        var party = fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(partyId);
        await party.CreateAsync(Guid.NewGuid(), players[0]);
        await party.JoinAsync(Guid.NewGuid(), players[1]);
        return (new(queueKey ?? NewQueue(), Guid.NewGuid(), MatchmakingOperationKind.EnqueueParty, partyId, players[0], false), party);
    }

    private async Task CancelAsync(MatchmakingOperationRequest request, Guid ticketId)
    {
        var cancellation = request with { RequestId = Guid.NewGuid(), Kind = MatchmakingOperationKind.Cancel, TargetId = ticketId };
        var result = await fixture.Cluster.GrainFactory.GetGrain<IMatchmakingOperationGrain>(cancellation.GetGrainKey()).ExecuteAsync(cancellation);
        Assert.Equal(MatchmakingOperationError.None, result.Error);
    }

    private async Task<Guid[]> PlayersAsync(int count = 4)
    {
        var players = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await fixture.RegisterPlayersAsync(players);
        return players;
    }

    private static string NewQueue() => $"recovery-{Guid.NewGuid():N}";
    private static MatchQueueEntryRequest Solo(Guid player) => new(Guid.NewGuid(), MatchQueueEntryKind.SoloPlayer, null, player, [player]);
    private GameRoomRecoveryProcessor RoomRecovery() => new(new Factory(fixture), fixture.Cluster.GrainFactory, _clock, NullLogger<GameRoomRecoveryProcessor>.Instance);
    private MatchmakingRecoveryProcessor MatchmakingRecovery() => new(new Factory(fixture), fixture.Cluster.GrainFactory, _clock, NullLogger<MatchmakingRecoveryProcessor>.Instance);

    /// <summary>테스트가 만든 고정 식별자와 GUID만 사용하며 외부 입력을 SQL에 넣지 않습니다.</summary>
    private Task<Trigger> FaultAsync(string table, string action, string condition, string sqlState = "P0001") =>
        CreateTriggerAsync(table, action, condition, $"RAISE EXCEPTION 'injected recovery test failure' USING ERRCODE = '{sqlState}';");

    private async Task<Trigger> CreateTriggerAsync(string table, string action, string condition, string body)
    {
        var name = $"recovery_fault_{Guid.NewGuid():N}";
        var sql = $"CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN {body} END $$; CREATE TRIGGER {name} BEFORE {action} ON {table} FOR EACH ROW WHEN ({condition}) EXECUTE FUNCTION {name}();";
        await using var context = fixture.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(sql);
        return new Trigger(fixture, table, name);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }

    private static CombatTestTimeProvider FreezeClock()
    {
        CombatTestTimeProvider.Shared.Set(DateTimeOffset.UtcNow);
        return CombatTestTimeProvider.Shared;
    }

    public void Dispose() => _clock.Reset();
    private sealed class Factory(OrleansTestClusterFixture fixture) : IDbContextFactory<GameDbContext>
    {
        public GameDbContext CreateDbContext() => fixture.CreateDbContext();
    }
    private sealed class Trigger(OrleansTestClusterFixture fixture, string table, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var context = fixture.CreateDbContext();
            var sql = $"DROP TRIGGER {name} ON {table}; DROP FUNCTION {name}();";
            await context.Database.ExecuteSqlRawAsync(sql);
        }
    }
}
