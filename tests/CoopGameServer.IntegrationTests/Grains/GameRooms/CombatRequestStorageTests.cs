using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.Grains.GameRooms;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>개발 DB 대신 일회용 PostgreSQL에서 전투 요청 변환·복원과 승인 순번 제약을 검증합니다.</summary>
[Collection(PostgreSqlIntegrationTestGroup.Name)]
public sealed class CombatRequestStorageTests(PostgreSqlDatabaseFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public async Task SuccessAndCooldownRejectionReplayAfterReadingNewContext()
    {
        var state = CreateState();
        var snapshot = state.Get()!;
        var first = new GameRoomCombatCommand(Guid.NewGuid(), snapshot.PlayerIds[0], 1, CombatActionKind.BasicAttack);
        state.ExecuteCombat(first, Now);
        var second = first with { RequestId = Guid.NewGuid(), Sequence = 2 };
        state.ExecuteCombat(second, Now);
        await using (var context = fixture.CreateDbContext())
        {
            context.GameRoomRequests.AddRange(state.GetStoredRequests().Select(request => GameRoomGrain.CreateRequestRecord(snapshot.RoomId, request)));
            await context.SaveChangesAsync();
        }

        await using var reader = fixture.CreateDbContext();
        var rows = await reader.GameRoomRequests.AsNoTracking().Where(r => r.RoomId == snapshot.RoomId).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Equal(1L, rows.Single(r => r.RequestId == first.RequestId).AcceptedCommandSequence);
        Assert.Null(rows.Single(r => r.RequestId == second.RequestId).AcceptedCommandSequence);
        // 현재 방은 메모리 스냅샷으로 전달합니다. Silo 전체 재시작 테스트가 아닌 요청 기록의 DB 왕복 검증입니다.
        var restored = GameRoomState.Restore(state.Get(), rows.Select(GameRoomGrain.RestoreStoredRequest));
        var replay = restored.ExecuteCombat(first, Now.AddHours(1));
        Assert.True(replay.IsReplay);
        Assert.Equal(GameRoomCombatError.None, replay.Error);
        Assert.Equal(80, replay.Room!.EnemyCurrentHealth);
        var refusal = restored.ExecuteCombat(second, Now.AddHours(1));
        Assert.True(refusal.IsReplay);
        Assert.Equal(GameRoomCombatError.CooldownActive, refusal.Error);
        Assert.Equal(Now.AddSeconds(1), refusal.RetryAt);
        Assert.Equal(GameRoomCombatError.None, restored.ExecuteCombat(second with { RequestId = Guid.NewGuid() }, Now.AddSeconds(1)).Error);
    }

    [Fact]
    public async Task DuplicateAcceptedSequenceIsRejectedButOtherRoomIsIndependent()
    {
        var room = Guid.NewGuid();
        var player = Guid.NewGuid();
        await using (var context = fixture.CreateDbContext())
        {
            context.GameRoomRequests.AddRange(Row(room, player, 1), Row(Guid.NewGuid(), player, 1));
            await context.SaveChangesAsync();
        }

        await using var duplicate = fixture.CreateDbContext();
        duplicate.GameRoomRequests.Add(Row(room, player, 1));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData("Unknown", true, 1L)]
    [InlineData("BasicAttack", false, 1L)]
    [InlineData("UseSkill", true, 0L)]
    [InlineData("Start", true, 1L)]
    public async Task InvalidShapesAreRejected(string kind, bool hasPlayer, long accepted)
    {
        await using var context = fixture.CreateDbContext();
        context.GameRoomRequests.Add(new GameRoomRequestRecord(Guid.NewGuid(), Guid.NewGuid(), kind, "{}", "{}", Now,
            hasPlayer ? Guid.NewGuid() : null, accepted));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public void MismatchedCombatColumnsCannotSilentlyRestore()
    {
        var state = CreateState();
        var command = new GameRoomCombatCommand(Guid.NewGuid(), state.Get()!.PlayerIds[0], 1, CombatActionKind.UseSkill);
        state.ExecuteCombat(command, Now);
        var record = GameRoomGrain.CreateRequestRecord(state.Get()!.RoomId, state.GetStoredRequest(command.RequestId)!);
        var damaged = new GameRoomRequestRecord(record.RequestId, record.RoomId, record.CommandKind,
            record.RequestPayloadJson, record.ResultPayloadJson, record.CreatedAt, Guid.NewGuid(), 1);
        Assert.Throws<InvalidOperationException>(() => GameRoomGrain.RestoreStoredRequest(damaged));
    }

    private static GameRoomRequestRecord Row(Guid room, Guid player, long sequence)
        => new(Guid.NewGuid(), room, "BasicAttack", "{}", "{}", Now, player, sequence);

    private static GameRoomState CreateState()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        return GameRoomState.Restore(new GameRoomSnapshot(Guid.NewGuid(), "storage-test", GameRoomLifecycle.InGame,
            [], ids, Now, Now, null, GameOutcome.None, 1, 1,
            ids.Select((id, i) => new PlayerCombatSnapshot(id, i, 100, 100, PlayerCombatStatus.Active, 0, null, null)).ToArray(),
            1, 3, 100, 100, 2, 0), []);
    }
}
