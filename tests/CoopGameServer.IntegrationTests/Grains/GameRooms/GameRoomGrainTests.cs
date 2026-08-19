using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Orleans.TestingHost;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>
/// GameRoomGrain의 4인 방 검증, 파티 상태 연동, 멱등성, PostgreSQL 복원 규칙을 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomGrainTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;
    private readonly TestCluster _cluster = fixture.Cluster;

    [Fact]
    public async Task CreateAsyncCreatesReadyRoomAndReplaysSameAssignment()
    {
        var assignment = CreateAssignment();
        var requestId = Guid.NewGuid();
        var room = GetRoom(assignment.RoomId);

        var firstResult = await room.CreateAsync(requestId, assignment);
        var replayResult = await room.CreateAsync(requestId, CloneAssignment(assignment));

        Assert.Equal(GameRoomCommandError.None, firstResult.Error);
        Assert.False(firstResult.IsReplay);
        Assert.True(replayResult.IsReplay);

        var snapshot = Assert.IsType<GameRoomSnapshot>(firstResult.Room);
        Assert.Equal(assignment.RoomId, snapshot.RoomId);
        Assert.Equal(GameRoomLifecycle.Ready, snapshot.Lifecycle);
        Assert.Empty(snapshot.PartyIds);
        Assert.Equal(assignment.PlayerIds, snapshot.PlayerIds);
        Assert.Null(snapshot.StartedAt);
        Assert.Null(snapshot.CompletedAt);
    }

    [Fact]
    public async Task CreateAsyncRejectsWrongRoomKeyAndInvalidPlayerCount()
    {
        var room = GetRoom(Guid.NewGuid());
        var wrongRoomAssignment = CreateAssignment();
        var invalidPlayersAssignment = CreateAssignment() with
        {
            RoomId = room.GetPrimaryKey(),
            PlayerIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
        };

        var wrongRoomResult = await room.CreateAsync(Guid.NewGuid(), wrongRoomAssignment);
        var invalidPlayersResult = await room.CreateAsync(Guid.NewGuid(), invalidPlayersAssignment);

        Assert.Equal(GameRoomCommandError.InvalidRoomId, wrongRoomResult.Error);
        Assert.Equal(GameRoomCommandError.InvalidPlayerIds, invalidPlayersResult.Error);
        Assert.Null(await room.GetAsync());
    }

    [Fact]
    public async Task StartAndCompleteKeepPreformedPartyMembersTogether()
    {
        var partyId = Guid.NewGuid();
        var playerIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var party = await CreateQueuedPartyAsync(partyId, playerIds[..3]);
        var assignment = CreateAssignment(partyIds: [partyId], playerIds: playerIds);
        var room = GetRoom(assignment.RoomId);
        await room.CreateAsync(Guid.NewGuid(), assignment);

        var startResult = await room.StartAsync(Guid.NewGuid());

        var inGameRoom = Assert.IsType<GameRoomSnapshot>(startResult.Room);
        var inGameParty = Assert.IsType<PartySnapshot>(await party.GetAsync());
        Assert.Equal(GameRoomLifecycle.InGame, inGameRoom.Lifecycle);
        Assert.NotNull(inGameRoom.StartedAt);
        Assert.Equal(PartyLifecycle.InGame, inGameParty.Lifecycle);
        Assert.Equal(assignment.RoomId, inGameParty.CurrentRoomId);

        var completeResult = await room.CompleteAsync(Guid.NewGuid());

        var completedRoom = Assert.IsType<GameRoomSnapshot>(completeResult.Room);
        var activeParty = Assert.IsType<PartySnapshot>(await party.GetAsync());
        Assert.Equal(GameRoomLifecycle.Completed, completedRoom.Lifecycle);
        Assert.NotNull(completedRoom.CompletedAt);
        Assert.Equal(PartyLifecycle.Active, activeParty.Lifecycle);
        Assert.Null(activeParty.CurrentRoomId);
        Assert.Equal(playerIds[..3], activeParty.MemberPlayerIds);
    }

    [Fact]
    public async Task FourSoloPlayersCanStartAndCompleteWithoutCreatingParties()
    {
        var assignment = CreateAssignment();
        var room = GetRoom(assignment.RoomId);
        await room.CreateAsync(Guid.NewGuid(), assignment);

        var startResult = await room.StartAsync(Guid.NewGuid());
        var completeResult = await room.CompleteAsync(Guid.NewGuid());

        Assert.Equal(GameRoomLifecycle.InGame, Assert.IsType<GameRoomSnapshot>(startResult.Room).Lifecycle);
        Assert.Equal(GameRoomLifecycle.Completed, Assert.IsType<GameRoomSnapshot>(completeResult.Room).Lifecycle);
        Assert.Empty(Assert.IsType<GameRoomSnapshot>(completeResult.Room).PartyIds);
    }

    [Fact]
    public async Task StartFailsAndKeepsRoomReadyWhenPartyIsNotQueued()
    {
        var partyId = Guid.NewGuid();
        var playerIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await CreateActivePartyAsync(partyId, playerIds[..2]);
        var assignment = CreateAssignment(partyIds: [partyId], playerIds: playerIds);
        var room = GetRoom(assignment.RoomId);
        await room.CreateAsync(Guid.NewGuid(), assignment);

        var result = await room.StartAsync(Guid.NewGuid());

        Assert.Equal(GameRoomCommandError.PartyTransitionFailed, result.Error);
        Assert.Equal(partyId, result.FailedPartyId);
        Assert.Equal(PartyCommandError.PartyNotMatchQueued, result.PartyError);
        Assert.Equal(GameRoomLifecycle.Ready, Assert.IsType<GameRoomSnapshot>(await room.GetAsync()).Lifecycle);
    }

    [Fact]
    public async Task StateAndOriginalStartResultSurviveSiloRestart()
    {
        var assignment = CreateAssignment();
        var createRequestId = Guid.NewGuid();
        var startRequestId = Guid.NewGuid();
        var room = GetRoom(assignment.RoomId);
        await room.CreateAsync(createRequestId, assignment);
        var firstStartResult = await room.StartAsync(startRequestId);

        // 실제 Silo 재시작으로 GameRoomGrain 메모리를 버리고 PostgreSQL 행에서 다시 복원합니다.
        await _cluster.RestartSiloAsync(_cluster.Primary);

        var restoredRoom = GetRoom(assignment.RoomId);
        var restoredSnapshot = Assert.IsType<GameRoomSnapshot>(await restoredRoom.GetAsync());
        var replayResult = await restoredRoom.StartAsync(startRequestId);
        var conflictResult = await restoredRoom.CompleteAsync(startRequestId);

        Assert.Equal(GameRoomLifecycle.InGame, restoredSnapshot.Lifecycle);
        Assert.True(replayResult.IsReplay);
        Assert.Equal(firstStartResult.Error, replayResult.Error);
        Assert.Equal(GameRoomCommandError.RequestIdConflict, conflictResult.Error);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.True(await gameDbContext.GameRooms.AnyAsync(record => record.RoomId == assignment.RoomId));
        Assert.Equal(
            2,
            await gameDbContext.GameRoomRequests.CountAsync(record => record.RoomId == assignment.RoomId));
    }

    [Fact]
    public async Task SameRequestIdCanCreateDifferentGameRooms()
    {
        var firstAssignment = CreateAssignment();
        var secondAssignment = CreateAssignment();
        var sharedRequestId = Guid.NewGuid();

        var firstResult = await GetRoom(firstAssignment.RoomId)
            .CreateAsync(sharedRequestId, firstAssignment);
        var secondResult = await GetRoom(secondAssignment.RoomId)
            .CreateAsync(sharedRequestId, secondAssignment);

        Assert.Equal(GameRoomCommandError.None, firstResult.Error);
        Assert.Equal(GameRoomCommandError.None, secondResult.Error);
        Assert.NotEqual(firstResult.Room?.RoomId, secondResult.Room?.RoomId);
    }

    /// <summary>주어진 Guid 키의 GameRoomGrain 참조를 테스트 Client에서 얻습니다.</summary>
    private IGameRoomGrain GetRoom(Guid roomId)
    {
        return _cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
    }

    /// <summary>멤버를 등록하고 Active 파티를 만든 뒤 그대로 반환합니다.</summary>
    private async Task<IPartyGrain> CreateActivePartyAsync(Guid partyId, Guid[] memberPlayerIds)
    {
        await _fixture.RegisterPlayersAsync(memberPlayerIds);
        var party = _cluster.GrainFactory.GetGrain<IPartyGrain>(partyId);
        var createResult = await party.CreateAsync(Guid.NewGuid(), memberPlayerIds[0]);
        Assert.Equal(PartyCommandError.None, createResult.Error);

        foreach (var memberPlayerId in memberPlayerIds.Skip(1))
        {
            var joinResult = await party.JoinAsync(Guid.NewGuid(), memberPlayerId);
            Assert.Equal(PartyCommandError.None, joinResult.Error);
        }

        return party;
    }

    /// <summary>Active 파티를 만든 뒤 리더 요청으로 MatchQueued 상태까지 전환합니다.</summary>
    private async Task<IPartyGrain> CreateQueuedPartyAsync(Guid partyId, Guid[] memberPlayerIds)
    {
        var party = await CreateActivePartyAsync(partyId, memberPlayerIds);
        var queueResult = await party.QueueForMatchAsync(Guid.NewGuid(), memberPlayerIds[0]);
        Assert.Equal(PartyCommandError.None, queueResult.Error);
        return party;
    }

    /// <summary>기본적으로 파티가 없는 고유한 4인 매칭 결과를 만듭니다.</summary>
    private static MatchAssignment CreateAssignment(
        Guid[]? partyIds = null,
        Guid[]? playerIds = null)
    {
        return new MatchAssignment(
            Guid.NewGuid(),
            $"room-test-{Guid.NewGuid():N}",
            partyIds ?? [],
            playerIds ?? Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>배열 참조까지 분리한 동일 매칭 결과를 만들어 멱등성 내용 비교를 검증합니다.</summary>
    private static MatchAssignment CloneAssignment(MatchAssignment assignment)
    {
        return assignment with
        {
            PartyIds = assignment.PartyIds.ToArray(),
            PlayerIds = assignment.PlayerIds.ToArray(),
        };
    }
}
