using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>경기별 참가자 상태의 생성·복사·DB 제약·재시작 복원을 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomPlayerStateTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;

    [Fact]
    public async Task CreatePersistsFourPlayersAndProtectsSnapshotArrays()
    {
        var assignment = Assignment();
        var room = Room(assignment.RoomId);
        var requestId = Guid.NewGuid();
        var result = await room.CreateAsync(requestId, assignment);
        var snapshot = Assert.IsType<GameRoomSnapshot>(result.Room);
        Assert.Equal(1, snapshot.CombatRuleVersion);
        var players = Assert.IsType<PlayerCombatSnapshot[]>(snapshot.Players);
        Assert.Equal(assignment.PlayerIds, players.Select(player => player.PlayerId));
        Assert.Equal(Enumerable.Range(0, 4), players.Select(player => player.PlayerOrder));
        Assert.All(players, player =>
        {
            Assert.Equal(100, player.MaxHealth);
            Assert.Equal(100, player.CurrentHealth);
            Assert.Equal(PlayerCombatStatus.Active, player.CombatStatus);
            Assert.Equal(0, player.LastAcceptedCommandSequence);
            Assert.Null(player.BasicAttackReadyAt);
            Assert.Null(player.SkillReadyAt);
        });

        // 호출자가 응답 배열을 바꾸어도 서버 상태와 최초 응답 이력은 바뀌지 않아야 합니다.
        players[0] = players[0] with { CurrentHealth = 1 };
        var replay = await room.CreateAsync(requestId, assignment);
        Assert.True(replay.IsReplay);
        Assert.Equal(100, replay.Room!.Players![0].CurrentHealth);
        Assert.Equal(100, (await room.GetAsync())!.Players![0].CurrentHealth);
        await using var context = _fixture.CreateDbContext();
        Assert.Equal(4, await context.GameRoomPlayers.CountAsync(player => player.RoomId == assignment.RoomId));
    }

    [Fact]
    public async Task RestartRestoresHealthOrderSequenceAndCooldownWithoutChangingOriginalResponse()
    {
        var assignment = Assignment();
        var createId = Guid.NewGuid();
        await Room(assignment.RoomId).CreateAsync(createId, assignment);
        // 실제 공격 명령은 아직 없으므로 복원 테스트용 저장 상태를 독립 DbContext로 준비합니다.
        var cooldown = DateTimeOffset.UtcNow.AddMinutes(1);
        await using (var context = _fixture.CreateDbContext())
        {
            var player = await context.GameRoomPlayers.SingleAsync(p => p.RoomId == assignment.RoomId && p.PlayerOrder == 0);
            player.Update(100, 75, 0, 7, cooldown, cooldown.AddSeconds(4));
            await context.SaveChangesAsync();
            // PostgreSQL은 마이크로초 정밀도이므로 저장된 시각을 비교 기준으로 사용합니다.
            await context.Entry(player).ReloadAsync();
            cooldown = player.BasicAttackReadyAt!.Value;
        }

        await _fixture.RestartAllSilosAsync();
        var restoredRoom = Room(assignment.RoomId);
        var snapshot = Assert.IsType<GameRoomSnapshot>(await restoredRoom.GetAsync());
        var restoredPlayers = Assert.IsType<PlayerCombatSnapshot[]>(snapshot.Players);
        Assert.Equal(assignment.PlayerIds, restoredPlayers.Select(player => player.PlayerId));
        Assert.Equal(75, restoredPlayers[0].CurrentHealth);
        Assert.Equal(7, restoredPlayers[0].LastAcceptedCommandSequence);
        Assert.Equal(cooldown, restoredPlayers[0].BasicAttackReadyAt);
        Assert.Equal(cooldown.AddSeconds(4), restoredPlayers[0].SkillReadyAt);
        Assert.Equal(100, (await restoredRoom.CreateAsync(createId, assignment)).Room!.Players![0].CurrentHealth);

        // 기존 시작 명령이 Player 상태를 초기화하지 않는지도 확인합니다.
        Assert.Equal(75, (await restoredRoom.StartAsync(Guid.NewGuid())).Room!.Players![0].CurrentHealth);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(101, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(10, 1, 0)]
    [InlineData(10, 0, -1)]
    public async Task DatabaseRejectsInvalidHealthStatusOrSequence(int health, int status, long sequence)
    {
        var assignment = Assignment();
        await Room(assignment.RoomId).CreateAsync(Guid.NewGuid(), assignment);
        await using var context = _fixture.CreateDbContext();
        var player = await context.GameRoomPlayers.SingleAsync(p => p.RoomId == assignment.RoomId && p.PlayerOrder == 0);
        player.Update(100, health, status, sequence, null, null);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task FailedCreationDoesNotLeavePartialParticipants()
    {
        var assignment = Assignment();
        var room = Room(assignment.RoomId);
        var requestId = Guid.NewGuid();
        Assert.Null(await room.GetAsync());
        await using var context = _fixture.CreateDbContext();
        // 메모리 활성화 이후 요청 키를 충돌시켜 방·참가자 삽입 트랜잭션을 실패시킵니다.
        context.GameRoomRequests.Add(new GameRoomRequestRecord(requestId, assignment.RoomId,
            "Start", null, "{}", DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        Assert.NotNull(await Record.ExceptionAsync(() => room.CreateAsync(requestId, assignment)));
        Assert.Null(await room.GetAsync());
        Assert.False(await context.GameRooms.AnyAsync(r => r.RoomId == assignment.RoomId));
        Assert.False(await context.GameRoomPlayers.AnyAsync(p => p.RoomId == assignment.RoomId));
        await context.GameRoomRequests.Where(r => r.RoomId == assignment.RoomId).ExecuteDeleteAsync();
        Assert.Equal(GameRoomCommandError.None, (await room.CreateAsync(requestId, assignment)).Error);
        Assert.Equal(4, await context.GameRoomPlayers.CountAsync(p => p.RoomId == assignment.RoomId));
    }

    [Fact]
    public async Task ActivationRejectsMissingParticipantInsteadOfResettingHealth()
    {
        var assignment = Assignment();
        await Room(assignment.RoomId).CreateAsync(Guid.NewGuid(), assignment);
        await using var context = _fixture.CreateDbContext();
        // 이 테스트 방만 손상시킵니다. 잘못된 복원 대신 명시적으로 실패해야 합니다.
        await context.GameRoomPlayers.Where(p => p.RoomId == assignment.RoomId && p.PlayerOrder == 3).ExecuteDeleteAsync();
        await _fixture.RestartAllSilosAsync();
        Assert.NotNull(await Record.ExceptionAsync(() => Room(assignment.RoomId).GetAsync()));
        Assert.Equal(3, await context.GameRoomPlayers.CountAsync(p => p.RoomId == assignment.RoomId));
    }

    private IGameRoomGrain Room(Guid id) => _fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(id);
    private static MatchAssignment Assignment() => new(Guid.NewGuid(), "coop-dungeon-normal-v1", [],
        Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(), DateTimeOffset.UtcNow);
}
