using CoopGameServer.Domain.GameRooms;

namespace CoopGameServer.UnitTests.Domain.GameRooms;

/// <summary>실제 대기 없이 시간 경계와 이전 연결 차단 규칙을 검사합니다.</summary>
public sealed class GameRoomConnectionRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly GameRoomConnectionRules Rules = GameRoomConnectionRules.Default;

    [Theory]
    [InlineData(14, RoomConnectionStatus.Connected)]
    [InlineData(15, RoomConnectionStatus.Disconnected)]
    [InlineData(44, RoomConnectionStatus.Disconnected)]
    [InlineData(45, RoomConnectionStatus.Abandoned)]
    [InlineData(600, RoomConnectionStatus.Abandoned)]
    public void ExpirationUsesOriginalDeadline(int seconds, RoomConnectionStatus expected)
    {
        var connected = Connect();
        var evaluated = Rules.Evaluate(connected, Now.AddSeconds(seconds));
        Assert.Equal(expected, evaluated.Status);
        if (seconds >= 15)
        {
            Assert.Equal(Now.AddSeconds(15), evaluated.DisconnectedAt);
            Assert.Null(evaluated.ConnectionId);
        }
        if (seconds >= 45)
        {
            Assert.Equal(Now.AddSeconds(45), evaluated.AbandonedAt);
            Assert.Null(evaluated.ConnectionId);
        }
        Assert.Equal(RoomConnectionStatus.Connected, connected.Status);
    }

    [Fact]
    public void HeartbeatExtendsOnlyCurrentLiveConnection()
    {
        var state = Connect();
        var result = Rules.Heartbeat(state, state.ConnectionId!.Value, 1, Now.AddSeconds(5));
        Assert.Equal(RoomConnectionError.None, result.Error);
        Assert.Equal(Now.AddSeconds(20), result.State.LeaseExpiresAt);
        Assert.Equal(1, result.State.Generation);
        Assert.Equal(RoomConnectionError.NotConnected, Rules.Heartbeat(state, state.ConnectionId.Value, 1, Now.AddSeconds(15)).Error);
        Assert.Equal(RoomConnectionError.StaleConnection, Rules.Heartbeat(state, Guid.NewGuid(), 1, Now).Error);
        Assert.Equal(RoomConnectionError.StaleConnection, Rules.Heartbeat(state, state.ConnectionId.Value, 2, Now).Error);
    }

    [Fact]
    public void ReconnectFencesOldConnectionAndCompetingGeneration()
    {
        var state = Connect();
        var result = Rules.Reconnect(state, 1, Guid.NewGuid(), Now.AddSeconds(20));
        Assert.Equal(RoomConnectionError.None, result.Error);
        Assert.Equal(2, result.State.Generation);
        Assert.Null(result.State.ReconnectDeadline);
        Assert.Equal(RoomConnectionError.StaleConnection, Rules.Reconnect(result.State, 1, Guid.NewGuid(), Now.AddSeconds(20)).Error);
        Assert.Equal(RoomConnectionError.StaleConnection, Rules.Heartbeat(result.State, state.ConnectionId!.Value, 1, Now.AddSeconds(20)).Error);
    }

    [Fact]
    public void DisconnectStartsGraceAndExactDeadlineRejectsReconnect()
    {
        var state = Connect();
        var disconnected = Rules.Disconnect(state, state.ConnectionId!.Value, 1, Now.AddSeconds(2)).State;
        Assert.Equal(Now.AddSeconds(32), disconnected.ReconnectDeadline);
        Assert.Null(disconnected.ConnectionId);
        Assert.Equal(RoomConnectionError.None, Rules.Reconnect(disconnected, 1, Guid.NewGuid(), Now.AddSeconds(31)).Error);
        Assert.Equal(RoomConnectionError.ReconnectNotAllowed, Rules.Reconnect(disconnected, 1, Guid.NewGuid(), Now.AddSeconds(32)).Error);
    }

    [Fact]
    public void IssuedCredentialCanOnlyReplayWhileStillCurrentAndLive()
    {
        var state = Connect();
        Assert.True(GameRoomConnectionRules.CanReplayCredential(state, state, Now));
        Assert.False(GameRoomConnectionRules.CanReplayCredential(state, state, Now.AddSeconds(15)));
        var replacement = Rules.Reconnect(state, 1, Guid.NewGuid(), Now).State;
        Assert.False(GameRoomConnectionRules.CanReplayCredential(replacement, state, Now));
        var closed = GameRoomConnectionRules.Close(state);
        Assert.Equal(RoomConnectionStatus.Left, closed.Status);
        Assert.Null(closed.ConnectionId);
        Assert.Equal(RoomConnectionStatus.Abandoned, GameRoomConnectionRules.Close(Rules.Evaluate(state, Now.AddMinutes(1))).Status);
    }

    [Fact]
    public void InvalidInputsAndClockRegressionDoNotIssueCredentials()
    {
        Assert.Equal(RoomConnectionError.InvalidConnectionId, Rules.Connect(new(), Guid.Empty, Now).Error);
        Assert.Equal(RoomConnectionError.TimeRangeExceeded, Rules.Connect(new(), Guid.NewGuid(), DateTimeOffset.MaxValue).Error);
        Assert.Equal(RoomConnectionError.GenerationExhausted, Rules.Connect(new(Generation: long.MaxValue), Guid.NewGuid(), Now).Error);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameRoomConnectionRules(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameRoomConnectionRules(TimeSpan.MaxValue, TimeSpan.FromSeconds(1)));
        var state = Connect();
        Assert.Equal(RoomConnectionError.AlreadyConnected, Rules.Connect(state, Guid.NewGuid(), Now).Error);
        Assert.Equal(RoomConnectionError.ServerClockMovedBackwards, Rules.Heartbeat(state, state.ConnectionId!.Value, 1, Now.AddSeconds(-1)).Error);
    }

    private static RoomPlayerConnection Connect() => Rules.Connect(new(), Guid.NewGuid(), Now).State;
}
