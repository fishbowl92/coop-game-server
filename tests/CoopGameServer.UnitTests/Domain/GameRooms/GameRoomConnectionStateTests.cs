using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.Grains.GameRooms;

namespace CoopGameServer.UnitTests.Domain.GameRooms;

/// <summary>연결 상태와 최초 요청 기록이 복사·재생 과정에서도 서로 오염되지 않는지 검사합니다.</summary>
public sealed class GameRoomConnectionStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void ConnectReplayDoesNotRotateAndSupersededCredentialIsHidden()
    {
        var state = Create();
        var player = state.Get()!.PlayerIds[0];
        var command = new GameRoomConnectionCommand(Guid.NewGuid(), player, GameRoomConnectionAction.Connect);
        var first = state.ExecuteConnection(command, Now, Guid.NewGuid());
        var replay = state.ExecuteConnection(command, Now, Guid.NewGuid());
        Assert.Equal("None", first.Error);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.ConnectionId, replay.ConnectionId);
        var replacement = state.ExecuteConnection(new(Guid.NewGuid(), player, GameRoomConnectionAction.Reconnect,
            Generation: 1), Now, Guid.NewGuid());
        Assert.Equal(2, replacement.Generation);
        var obsolete = state.ExecuteConnection(command, Now, Guid.NewGuid());
        Assert.Equal("SupersededReconnectRequest", obsolete.Error);
        Assert.Null(obsolete.ConnectionId);
    }

    [Fact]
    public void HeartbeatDoesNotAppendHistoryAndCloneDoesNotMutateOriginal()
    {
        var state = Create();
        var player = state.Get()!.PlayerIds[0];
        var connection = state.ExecuteConnection(new(Guid.NewGuid(), player, GameRoomConnectionAction.Connect), Now, Guid.NewGuid());
        var before = state.GetStoredRequests().Length;
        var candidate = state.Clone();
        var result = candidate.ExecuteConnection(new(Guid.Empty, player, GameRoomConnectionAction.Heartbeat,
            connection.ConnectionId!.Value, 1), Now.AddSeconds(5), Guid.NewGuid());
        Assert.Equal("None", result.Error);
        Assert.Equal(before, candidate.GetStoredRequests().Length);
        Assert.Equal(Now.AddSeconds(15), state.GetConnection(player).LeaseExpiresAt);
        Assert.Equal(Now.AddSeconds(20), candidate.GetConnection(player).LeaseExpiresAt);
        Assert.Equal(state.Get()!.StateVersion, candidate.Get()!.StateVersion);
    }

    [Fact]
    public void StartRequiresFourLiveConnectionsAndReceiptKeepsCredentialContract()
    {
        var state = Create();
        var players = state.Get()!.PlayerIds;
        var first = state.ExecuteConnection(new(Guid.NewGuid(), players[0], GameRoomConnectionAction.Connect), Now, Guid.NewGuid());
        var start = new GameRoomConnectionCommand(Guid.NewGuid(), players[0], GameRoomConnectionAction.StartCombat, first.ConnectionId!.Value, 1);
        Assert.Equal("StartConditionsNotMet", state.ExecuteConnection(start, Now, Guid.NewGuid()).Error);
        foreach (var player in players.Skip(1))
            state.ExecuteConnection(new(Guid.NewGuid(), player, GameRoomConnectionAction.Connect), Now, Guid.NewGuid());
        // 실패한 최초 결과를 덮어쓰지 않고 새 요청 키로 시작합니다.
        Assert.Equal("StartConditionsNotMet", state.ExecuteConnection(start, Now, Guid.NewGuid()).Error);
        start = start with { RequestId = Guid.NewGuid() };
        Assert.Equal("None", state.ExecuteConnection(start, Now, Guid.NewGuid()).Error);
        Assert.True(state.ExecuteConnection(start, Now, Guid.NewGuid()).IsReplay);
        Assert.Equal(GameRoomLifecycle.InGame, state.Get()!.Lifecycle);
    }

    [Fact]
    public void NonParticipantCannotReadReceiptAndOtherCommandCannotReuseKey()
    {
        var state = Create();
        var player = state.Get()!.PlayerIds[0];
        var command = new GameRoomConnectionCommand(Guid.NewGuid(), player, GameRoomConnectionAction.Connect);
        state.ExecuteConnection(command, Now, Guid.NewGuid());
        var stranger = state.ExecuteConnection(command with { PlayerId = Guid.NewGuid() }, Now, Guid.NewGuid());
        Assert.Equal("PlayerNotInRoom", stranger.Error);
        Assert.Null(stranger.Room);
        Assert.Null(stranger.ConnectionId);
        Assert.Equal("RequestIdConflict", state.ExecuteConnection(command with { Action = GameRoomConnectionAction.Reconnect }, Now, Guid.NewGuid()).Error);
        Assert.Equal(GameRoomCommandError.RequestIdConflict, state.Start(command.RequestId, Now).Error);
    }

    private static GameRoomState Create()
    {
        var state = new GameRoomState();
        var room = Guid.NewGuid();
        state.Create(room, Guid.NewGuid(), new MatchAssignment(room, "coop-dungeon-normal-v1", [],
            Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(), Now));
        return state;
    }
}
