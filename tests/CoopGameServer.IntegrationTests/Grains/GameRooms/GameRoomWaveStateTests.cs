using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>웨이브 초기화·상태 버전·재생·재시작·DB 제약을 실제 PostgreSQL에서 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomWaveStateTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;

    [Fact]
    public async Task OnlySuccessfulStateChangesAdvanceVersionAndStartInitializesFirstWave()
    {
        var assignment = Assignment();
        await _fixture.RegisterPlayersAsync(assignment.PlayerIds);
        var room = Room(assignment.RoomId);
        var createId = Guid.NewGuid();
        var startId = Guid.NewGuid();
        var completeId = Guid.NewGuid();
        var created = Assert.IsType<GameRoomSnapshot>((await room.CreateAsync(createId, assignment)).Room);
        Assert.Equal(1, created.StateVersion);
        Assert.Equal(0, created.CurrentWave);
        Assert.Equal(3, created.MaxWaves);
        Assert.Equal(0, created.EnemyCurrentHealth);
        var started = Assert.IsType<GameRoomSnapshot>((await room.StartAsync(startId)).Room);
        Assert.Equal(2, started.StateVersion);
        Assert.Equal(1, started.CurrentWave);
        Assert.Equal(100, started.EnemyMaxHealth);
        Assert.Equal(100, started.EnemyCurrentHealth);
        Assert.Equal(0, started.EnemyAttackSequence);

        Assert.Equal(GameRoomCommandError.RoomAlreadyStarted, (await room.StartAsync(Guid.NewGuid())).Error);
        Assert.Equal(GameRoomCommandError.InvalidOutcome, (await room.CompleteAsync(Guid.NewGuid(), GameOutcome.None)).Error);
        Assert.Equal(GameRoomCommandError.RequestIdConflict, (await room.CompleteAsync(startId, GameOutcome.Defeat)).Error);
        Assert.Equal(2, (await room.GetAsync())!.StateVersion);
        Assert.Equal(2, (await room.StartAsync(startId)).Room!.StateVersion);

        var completed = Assert.IsType<GameRoomSnapshot>((await room.CompleteAsync(completeId, GameOutcome.Victory)).Room);
        Assert.Equal(3, completed.StateVersion);
        // 현재 결과 지정 경로는 적을 처치한 척 마지막 웨이브·체력 0을 만들지 않습니다.
        Assert.Equal(1, completed.CurrentWave);
        Assert.Equal(100, completed.EnemyCurrentHealth);
        Assert.Equal(0, completed.EnemyAttackSequence);
        Assert.Equal(3, (await room.CompleteAsync(completeId, GameOutcome.Victory)).Room!.StateVersion);
        Assert.Equal(1, (await room.CreateAsync(createId, assignment)).Room!.StateVersion);
        Assert.Equal(2, (await room.StartAsync(startId)).Room!.StateVersion);
        Assert.Equal(3, (await room.GetAsync())!.StateVersion);
    }

    [Fact]
    public async Task RestartRestoresProgressWithoutReinitializingOrRewritingOriginalStart()
    {
        var assignment = Assignment();
        var room = Room(assignment.RoomId);
        var startId = Guid.NewGuid();
        await room.CreateAsync(Guid.NewGuid(), assignment);
        await room.StartAsync(startId);
        // 공격 명령은 후속 단계이므로 복원용 진행 상태만 테스트 DB에서 준비합니다.
        await using (var context = _fixture.CreateDbContext())
        {
            var record = await context.GameRooms.SingleAsync(r => r.RoomId == assignment.RoomId);
            record.UpdateCombatProgress(2, 3, 180, 70, 7, 3);
            await context.SaveChangesAsync();
        }

        await _fixture.RestartAllSilosAsync();
        var restored = Room(assignment.RoomId);
        var snapshot = Assert.IsType<GameRoomSnapshot>(await restored.GetAsync());
        Assert.Equal(2, snapshot.CurrentWave);
        Assert.Equal(180, snapshot.EnemyMaxHealth);
        Assert.Equal(70, snapshot.EnemyCurrentHealth);
        Assert.Equal(7, snapshot.StateVersion);
        Assert.Equal(3, snapshot.EnemyAttackSequence);
        var replay = await restored.StartAsync(startId);
        Assert.True(replay.IsReplay);
        Assert.Equal(1, replay.Room!.CurrentWave);
        Assert.Equal(2, replay.Room.StateVersion);
        Assert.Equal(7, (await restored.GetAsync())!.StateVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionExhaustionRejectsStartOrCompletionWithoutOverflow(bool alreadyStarted)
    {
        var assignment = Assignment();
        var room = Room(assignment.RoomId);
        await room.CreateAsync(Guid.NewGuid(), assignment);
        if (alreadyStarted)
        {
            await room.StartAsync(Guid.NewGuid());
        }

        await using (var context = _fixture.CreateDbContext())
        {
            // 실행으로 최댓값까지 증가시킬 수 없으므로 경계 상태를 직접 준비합니다.
            var record = await context.GameRooms.SingleAsync(r => r.RoomId == assignment.RoomId);
            record.UpdateCombatProgress(record.CurrentWave, record.MaxWaves, record.EnemyMaxHealth,
                record.EnemyCurrentHealth, long.MaxValue, record.EnemyAttackSequence);
            await context.SaveChangesAsync();
        }

        await _fixture.RestartAllSilosAsync();
        var restored = Room(assignment.RoomId);
        var id = Guid.NewGuid();
        var result = alreadyStarted
            ? await restored.CompleteAsync(id, GameOutcome.Defeat)
            : await restored.StartAsync(id);
        Assert.Equal(GameRoomCommandError.StateVersionExhausted, result.Error);
        Assert.Equal(long.MaxValue, result.Room!.StateVersion);
        var replay = alreadyStarted
            ? await restored.CompleteAsync(id, GameOutcome.Defeat)
            : await restored.StartAsync(id);
        Assert.True(replay.IsReplay);
        Assert.Equal(GameRoomCommandError.StateVersionExhausted, replay.Error);
        var current = Assert.IsType<GameRoomSnapshot>(await restored.GetAsync());
        Assert.Equal(long.MaxValue, current.StateVersion);
        Assert.Equal(alreadyStarted ? GameRoomLifecycle.InGame : GameRoomLifecycle.Ready, current.Lifecycle);
    }

    [Theory]
    [InlineData(4, 3, 100, 100, 2, 0)]
    [InlineData(1, 2, 100, 100, 2, 0)]
    [InlineData(1, 3, 100, -1, 2, 0)]
    [InlineData(1, 3, 100, 101, 2, 0)]
    [InlineData(1, 3, 100, 100, 0, 0)]
    [InlineData(1, 3, 100, 100, 2, -1)]
    public async Task DatabaseRejectsInvalidProgress(int wave, int maxWaves, int maxHealth,
        int health, long version, long sequence)
    {
        var assignment = Assignment();
        var room = Room(assignment.RoomId);
        await room.CreateAsync(Guid.NewGuid(), assignment);
        await room.StartAsync(Guid.NewGuid());
        await using var context = _fixture.CreateDbContext();
        var record = await context.GameRooms.SingleAsync(r => r.RoomId == assignment.RoomId);
        record.UpdateCombatProgress(wave, maxWaves, maxHealth, health, version, sequence);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    private IGameRoomGrain Room(Guid id) => _fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(id);
    private static MatchAssignment Assignment() => new(Guid.NewGuid(), "coop-dungeon-normal-v1", [],
        Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(), DateTimeOffset.UtcNow);
}
