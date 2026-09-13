using CoopGameServer.Domain.GameRooms;
using CoopGameServer.GrainContracts.GameRooms;

namespace CoopGameServer.Grains.GameRooms;

internal sealed partial class GameRoomState
{
    /// <summary>연결 명령의 후보를 계산합니다. 호출 계층은 DB 커밋 후에만 이 복사본을 채택합니다.</summary>
    internal GameRoomConnectionResult ExecuteConnection(GameRoomConnectionCommand command, DateTimeOffset now, Guid newId)
    {
        if (command.PlayerId == Guid.Empty || !Enum.IsDefined(command.Action)
            || (command.Action != GameRoomConnectionAction.Heartbeat && command.RequestId == Guid.Empty))
            return new("InvalidCommand", false, null);
        if (_room is null) return new("RoomNotCreated", false, null);
        if (!_room.PlayerIds.Contains(command.PlayerId)) return new("PlayerNotInRoom", false, null);

        var current = GetConnection(command.PlayerId);
        if (command.Action != GameRoomConnectionAction.Heartbeat && _requests.TryGetValue(command.RequestId, out var stored))
        {
            if (stored.ConnectionCommand != command || stored.ConnectionResult is not { } original)
                return new("RequestIdConflict", false, Get());
            if (original.Error == "None" && command.Action is GameRoomConnectionAction.Connect or GameRoomConnectionAction.Reconnect
                && (!GameRoomConnectionRules.CanReplayCredential(current,
                    new(ConnectionId: original.ConnectionId, Generation: original.Generation), now)
                    || _room.Lifecycle == GameRoomLifecycle.Completed))
                return new("SupersededReconnectRequest", true, Get(), Generation: current.Generation);
            return original with { IsReplay = true, Room = CloneSnapshot(original.Room) };
        }

        GameRoomConnectionResult Remember(string error, RoomPlayerConnection connection)
        {
            var result = new GameRoomConnectionResult(error, false, Get(),
                error == "None" && command.Action is GameRoomConnectionAction.Connect or GameRoomConnectionAction.Reconnect
                    ? connection.ConnectionId : null, connection.Generation,
                error == "None" ? connection.LeaseExpiresAt : null);
            // 고빈도 생존 신호는 현재 행만 갱신하고 영구 요청 이력을 남기지 않습니다.
            if (command.Action != GameRoomConnectionAction.Heartbeat)
                _requests.Add(command.RequestId, new(command.RequestId, GameRoomCommandKind.Connection, null, null,
                    Success(), now, ConnectionCommand: command, ConnectionResult: result));
            return result;
        }

        if (_room.Lifecycle == GameRoomLifecycle.Completed) return Remember("RoomCompleted", current);
        if (_room.StateVersion == long.MaxValue) return Remember("StateVersionExhausted", current);
        if (command.Generation < 0) return Remember("InvalidConnectionGeneration", current);

        var rules = GameRoomConnectionRules.Default;
        if (command.Action == GameRoomConnectionAction.StartCombat)
        {
            if (_room.Lifecycle != GameRoomLifecycle.Ready) return Remember("RoomAlreadyStarted", current);
            if (current.Status != RoomConnectionStatus.Connected || current.ConnectionId != command.ConnectionId
                || current.Generation != command.Generation || current.LeaseExpiresAt <= now)
                return Remember("StaleConnection", current);
            if (_room.PlayerIds.Any(id => GetConnection(id).Status != RoomConnectionStatus.Connected
                || GetConnection(id).LeaseExpiresAt <= now)) return Remember("StartConditionsNotMet", current);
            // 내부 Start 기록 대신 연결 자격을 포함한 최초 계약과 결과를 보존합니다.
            var start = Start(command.RequestId, now);
            _requests.Remove(command.RequestId);
            return Remember(start.Error.ToString(), current);
        }

        var transition = command.Action switch
        {
            GameRoomConnectionAction.Connect => rules.Connect(current, newId, now),
            GameRoomConnectionAction.Reconnect => rules.Reconnect(current, command.Generation, newId, now),
            GameRoomConnectionAction.Heartbeat => rules.Heartbeat(current, command.ConnectionId, command.Generation, now),
            GameRoomConnectionAction.Disconnect => rules.Disconnect(current, command.ConnectionId, command.Generation, now),
            _ => throw new InvalidOperationException("검증되지 않은 연결 명령입니다."),
        };
        _connections[command.PlayerId] = transition.State;
        if (transition.State.Status != current.Status || transition.State.Generation != current.Generation)
            _room = _room with { StateVersion = _room.StateVersion + 1 };
        return Remember(transition.Error.ToString(), transition.State);
    }
}
