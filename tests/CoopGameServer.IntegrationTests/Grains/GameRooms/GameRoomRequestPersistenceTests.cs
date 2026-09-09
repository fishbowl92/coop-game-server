using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>
/// 실제 PostgreSQL과 Orleans를 사용해 요청 이력의 추가 전용 저장 및 실패 시 원자성을 검증합니다.
/// 개발용 DB가 아니라 OrleansTestClusterFixture가 만든 일회성 테스트 DB만 사용합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomRequestPersistenceTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;

    [Fact]
    public async Task CommandsAppendOneRequestWithoutRewritingEarlierRows()
    {
        var assignment = CreateAssignment();
        await _fixture.RegisterPlayersAsync(assignment.PlayerIds);
        var room = GetRoom(assignment.RoomId);
        var createId = Guid.NewGuid();
        var startId = Guid.NewGuid();
        var completeId = Guid.NewGuid();

        Assert.Equal(GameRoomCommandError.None, (await room.CreateAsync(createId, assignment)).Error);
        var originalCreate = await ReadRequestAsync(assignment.RoomId, createId);
        await AssertRequestCountAsync(assignment.RoomId, 1);

        Assert.Equal(GameRoomCommandError.None, (await room.StartAsync(startId)).Error);
        var originalStart = await ReadRequestAsync(assignment.RoomId, startId);
        await AssertRequestCountAsync(assignment.RoomId, 2);
        Assert.Equal(originalCreate, await ReadRequestAsync(assignment.RoomId, createId));

        Assert.Equal(GameRoomCommandError.None, (await room.CompleteAsync(completeId, GameOutcome.Defeat)).Error);
        await AssertRequestCountAsync(assignment.RoomId, 3);
        Assert.Equal(originalCreate, await ReadRequestAsync(assignment.RoomId, createId));
        Assert.Equal(originalStart, await ReadRequestAsync(assignment.RoomId, startId));

        await using var context = _fixture.CreateDbContext();
        var results = await context.GameResults.Where(result => result.RoomId == assignment.RoomId).ToArrayAsync();
        Assert.Equal(4, results.Length);
        Assert.All(results, result => Assert.Equal(GameResultDeliveryStatus.NoReward, result.DeliveryStatus));
    }

    [Fact]
    public async Task ReplaysAndKeyConflictsPreserveOriginalRequests()
    {
        var assignment = CreateAssignment();
        await _fixture.RegisterPlayersAsync(assignment.PlayerIds);
        var room = GetRoom(assignment.RoomId);
        var createId = Guid.NewGuid();
        var startId = Guid.NewGuid();
        var completeId = Guid.NewGuid();
        await room.CreateAsync(createId, assignment);
        await room.StartAsync(startId);
        await room.CompleteAsync(completeId, GameOutcome.Victory);
        var originalCreate = await ReadRequestAsync(assignment.RoomId, createId);
        var originalStart = await ReadRequestAsync(assignment.RoomId, startId);
        var originalComplete = await ReadRequestAsync(assignment.RoomId, completeId);

        // 현재 상태가 아니라 각 requestId를 최초 처리한 시점의 결과를 반환해야 합니다.
        var createReplay = await room.CreateAsync(createId, assignment);
        var startReplay = await room.StartAsync(startId);
        var completeReplay = await room.CompleteAsync(completeId, GameOutcome.Victory);
        Assert.True(createReplay.IsReplay);
        Assert.Equal(GameRoomLifecycle.Ready, createReplay.Room?.Lifecycle);
        Assert.True(startReplay.IsReplay);
        Assert.Equal(GameRoomLifecycle.InGame, startReplay.Room?.Lifecycle);
        Assert.True(completeReplay.IsReplay);
        Assert.Equal(GameOutcome.Victory, completeReplay.Room?.Outcome);

        Assert.Equal(GameRoomCommandError.RequestIdConflict,
            (await room.CreateAsync(createId, assignment with { QueueKey = "changed-mode" })).Error);
        Assert.Equal(GameRoomCommandError.RequestIdConflict, (await room.StartAsync(createId)).Error);
        Assert.Equal(GameRoomCommandError.RequestIdConflict,
            (await room.CompleteAsync(completeId, GameOutcome.Defeat)).Error);
        await AssertRequestCountAsync(assignment.RoomId, 3);
        Assert.Equal(originalCreate, await ReadRequestAsync(assignment.RoomId, createId));
        Assert.Equal(originalStart, await ReadRequestAsync(assignment.RoomId, startId));
        Assert.Equal(originalComplete, await ReadRequestAsync(assignment.RoomId, completeId));
    }

    [Fact]
    public async Task RejectedRequestSurvivesRoomCreationAndSiloRestart()
    {
        var assignment = CreateAssignment();
        var room = GetRoom(assignment.RoomId);
        var rejectedId = Guid.NewGuid();
        var createId = Guid.NewGuid();

        // 아직 방이 없어도 유효한 요청 키의 도메인 거부 결과는 한 번 저장합니다.
        Assert.Equal(GameRoomCommandError.RoomNotCreated, (await room.StartAsync(rejectedId)).Error);
        var originalRejection = await ReadRequestAsync(assignment.RoomId, rejectedId);
        await AssertRequestCountAsync(assignment.RoomId, 1);
        Assert.Equal(GameRoomCommandError.None, (await room.CreateAsync(createId, assignment)).Error);

        await _fixture.RestartAllSilosAsync();

        var restoredRoom = GetRoom(assignment.RoomId);
        var replay = await restoredRoom.StartAsync(rejectedId);
        Assert.True(replay.IsReplay);
        Assert.Equal(GameRoomCommandError.RoomNotCreated, replay.Error);
        Assert.Null(replay.Room);
        Assert.Equal(GameRoomLifecycle.Ready, (await restoredRoom.GetAsync())?.Lifecycle);

        // 재시작 이후 새 키로 시작하는 요청만 추가됩니다. 최초 거부 결과는 성공으로 바뀌지 않습니다.
        Assert.Equal(GameRoomCommandError.None, (await restoredRoom.StartAsync(Guid.NewGuid())).Error);
        await AssertRequestCountAsync(assignment.RoomId, 3);
        Assert.Equal(originalRejection, await ReadRequestAsync(assignment.RoomId, rejectedId));
    }

    [Fact]
    public async Task InputsWithoutStoredResultsDoNotCreateRequestRows()
    {
        var assignment = CreateAssignment();
        var room = GetRoom(assignment.RoomId);

        // 빈 키 및 null 배정은 요청 기록을 만들지 않는 기존 입력 검증 정책을 유지합니다.
        Assert.Equal(GameRoomCommandError.InvalidRequestId, (await room.CreateAsync(Guid.Empty, assignment)).Error);
        Assert.Equal(GameRoomCommandError.InvalidRequestId, (await room.StartAsync(Guid.Empty)).Error);
        Assert.Equal(GameRoomCommandError.InvalidRequestId,
            (await room.CompleteAsync(Guid.Empty, GameOutcome.Defeat)).Error);
        Assert.Equal(GameRoomCommandError.InvalidRoomId, (await room.CreateAsync(Guid.NewGuid(), null!)).Error);
        Assert.Null(await room.GetAsync());
        await AssertRequestCountAsync(assignment.RoomId, 0);
    }

    [Fact]
    public async Task FailedInsertPreservesDatabaseAndMemoryAndAllowsRetry()
    {
        var assignment = CreateAssignment();
        var room = GetRoom(assignment.RoomId);
        var createId = Guid.NewGuid();
        var startId = Guid.NewGuid();
        await room.CreateAsync(createId, assignment);
        var originalCreate = await ReadRequestAsync(assignment.RoomId, createId);

        // 테스트 전용 장애 주입: 활성 Grain이 모르는 동일 키의 DB 행을 미리 만들어 PK 충돌을 유발합니다.
        // 애플리케이션에서 이런 DB 직접 쓰기를 허용한다는 뜻이 아닙니다.
        await using (var context = _fixture.CreateDbContext())
        {
            context.GameRoomRequests.Add(new GameRoomRequestRecord(
                startId, assignment.RoomId, "Start", null, "{}", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var injectedRow = await ReadRequestAsync(assignment.RoomId, startId);
        // DB 예외는 Orleans 전송 과정에서 다른 예외로 감싸질 수 있으므로 정확한 CLR 타입에 의존하지 않습니다.
        // 이 테스트의 판정 대상은 요청 실패 여부와 아래의 DB·메모리 원복, 같은 키 재시도 성공입니다.
        var exception = await Record.ExceptionAsync(() => room.StartAsync(startId));
        Assert.NotNull(exception);
        Assert.Equal(GameRoomLifecycle.Ready, (await room.GetAsync())?.Lifecycle);
        Assert.Equal(originalCreate, await ReadRequestAsync(assignment.RoomId, createId));
        Assert.Equal(injectedRow, await ReadRequestAsync(assignment.RoomId, startId));

        await using (var context = _fixture.CreateDbContext())
        {
            var persistedRoom = await context.GameRooms.SingleAsync(record => record.RoomId == assignment.RoomId);
            Assert.Equal((int)GameRoomLifecycle.Ready, persistedRoom.Lifecycle);
            Assert.Null(persistedRoom.StartedAt);
            Assert.Equal(1, persistedRoom.StateVersion);
            Assert.Equal(0, persistedRoom.CurrentWave);
            Assert.Equal(0, persistedRoom.EnemyCurrentHealth);
            Assert.Equal(1, (await room.GetAsync())!.StateVersion);

            // 장애를 제거하기 위해 이 테스트에서 주입한 한 행만 삭제합니다. 정상 요청 이력은 보존합니다.
            await context.GameRoomRequests
                .Where(record => record.RoomId == assignment.RoomId && record.RequestId == startId)
                .ExecuteDeleteAsync();
        }

        var retry = await room.StartAsync(startId);
        Assert.Equal(GameRoomCommandError.None, retry.Error);
        Assert.False(retry.IsReplay);
        Assert.Equal(GameRoomLifecycle.InGame, retry.Room?.Lifecycle);
        Assert.True((await room.StartAsync(startId)).IsReplay);
        await AssertRequestCountAsync(assignment.RoomId, 2);
    }

    /// <summary>
    /// JSON 내용뿐 아니라 PostgreSQL xmin(행 버전을 생성한 트랜잭션 번호)도 읽습니다.
    /// 같은 값으로 삭제·재삽입해도 내용 비교만으로는 검출할 수 없지만 xmin은 달라집니다.
    /// 여기서는 짧은 테스트 구간의 동일성 확인에만 사용하며 애플리케이션의 버전 번호로 쓰지 않습니다.
    /// </summary>
    private async Task<(string RowVersion, string? Request, string Result, DateTimeOffset CreatedAt)> ReadRequestAsync(
        Guid roomId, Guid requestId)
    {
        await using var context = _fixture.CreateDbContext();
        var row = await context.GameRoomRequests.AsNoTracking()
            .SingleAsync(record => record.RoomId == roomId && record.RequestId == requestId);
        var rowVersion = await context.Database.SqlQuery<string>($"""
            SELECT xmin::text AS "Value" FROM game_room_requests
            WHERE room_id = {roomId} AND request_id = {requestId}
            """).SingleAsync();
        return (rowVersion, row.RequestPayloadJson, row.ResultPayloadJson, row.CreatedAt);
    }

    /// <summary>이번 방의 요청 행 수만 조회해 다른 테스트의 데이터와 분리합니다.</summary>
    private async Task AssertRequestCountAsync(Guid roomId, int expectedCount)
    {
        await using var context = _fixture.CreateDbContext();
        Assert.Equal(expectedCount, await context.GameRoomRequests.CountAsync(record => record.RoomId == roomId));
    }

    private IGameRoomGrain GetRoom(Guid roomId) => _fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);

    /// <summary>파티의 외부 상태 전이와 무관하게 저장 동작에 집중할 수 있도록 솔로 네 명을 배정합니다.</summary>
    private static MatchAssignment CreateAssignment() => new(
        Guid.NewGuid(), "coop-dungeon-normal-v1", [],
        Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(), DateTimeOffset.UtcNow);
}
