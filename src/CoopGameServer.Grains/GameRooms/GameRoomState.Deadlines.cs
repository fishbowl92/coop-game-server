using CoopGameServer.Domain.GameRooms;
using CoopGameServer.GrainContracts.GameRooms;

namespace CoopGameServer.Grains.GameRooms;

internal sealed partial class GameRoomState
{
    /// <summary>절대 시각을 일괄 평가합니다. 여러 번 호출해도 같은 만료를 다시 적용하지 않습니다.</summary>
    internal bool EvaluateDeadlines(DateTimeOffset now)
    {
        if (_room is null || _room.Lifecycle == GameRoomLifecycle.Completed) return false;
        var changed = false;
        var players = _room.Players!.ToArray();
        for (var index = 0; index < players.Length; index++)
        {
            var player = players[index];
            var previous = GetConnection(player.PlayerId);
            var current = GameRoomConnectionRules.Default.Evaluate(previous, now);
            // 도입 전 InGame 방의 AwaitingConnection도 영원히 남지 않도록 최초 유예 만료를 처리합니다.
            if (_room.Lifecycle == GameRoomLifecycle.InGame && current.Status == RoomConnectionStatus.AwaitingConnection
                && _room.InitialConnectDeadline is { } initial && now >= initial)
                current = current with { Status = RoomConnectionStatus.Abandoned, AbandonedAt = initial };
            if (current != previous)
            {
                _connections[player.PlayerId] = current;
                changed = true;
            }
            if (_room.Lifecycle == GameRoomLifecycle.InGame && current.Status == RoomConnectionStatus.Abandoned
                && player.CombatStatus != PlayerCombatStatus.Incapacitated)
            {
                players[index] = player with { CurrentHealth = 0, CombatStatus = PlayerCombatStatus.Incapacitated };
                changed = true;
            }
        }

        var expiredInitial = _room.Lifecycle == GameRoomLifecycle.Ready
            && _room.InitialConnectDeadline is { } deadline && now >= deadline;
        var defeated = _room.Lifecycle == GameRoomLifecycle.InGame && players.All(p => p.CurrentHealth == 0);
        if (!changed && !expiredInitial && !defeated) return false;
        if (_room.StateVersion == long.MaxValue) throw new InvalidOperationException("방 상태 버전이 소진되어 만료를 저장할 수 없습니다.");
        _room = _room with { Players = players, StateVersion = _room.StateVersion + 1 };
        if (expiredInitial || defeated)
        {
            _room = _room with
            {
                Lifecycle = GameRoomLifecycle.Completed,
                CompletedAt = now,
                Outcome = expiredInitial ? GameOutcome.Cancelled : GameOutcome.Defeat,
                CancellationReason = expiredInitial ? "InitialConnectionTimeout" : null
            };
            CloseConnections();
        }
        return true;
    }

    /// <summary>경기 종료 후 재사용할 수 없도록 모든 연결 자격을 폐기합니다.</summary>
    internal void CloseConnections()
    {
        foreach (var id in _room?.PlayerIds ?? [])
            _connections[id] = GameRoomConnectionRules.Close(GetConnection(id));
    }
}
