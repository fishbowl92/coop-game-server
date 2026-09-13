namespace CoopGameServer.Domain.GameRooms;

/// <summary>HTTP 요청에서 직접 소켓 종료를 알 수 없으므로 서버의 절대 시각으로 논리적 연결을 판단합니다.</summary>
public sealed class GameRoomConnectionRules
{
    public GameRoomConnectionRules(TimeSpan leaseDuration, TimeSpan graceDuration)
    {
        if (leaseDuration <= TimeSpan.Zero || graceDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "연결 유지와 복귀 유예 시간은 양수여야 합니다.");
        }
        if (leaseDuration.Ticks > TimeSpan.MaxValue.Ticks - graceDuration.Ticks)
            throw new ArgumentOutOfRangeException(nameof(graceDuration), "두 시간의 합이 표현 범위를 초과합니다.");
        LeaseDuration = leaseDuration;
        GraceDuration = graceDuration;
    }

    /// <summary>운영 설정과 분리한 학습용 기본값입니다. 호출자는 필요한 정책을 생성자로 주입할 수 있습니다.</summary>
    public static GameRoomConnectionRules Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
    public TimeSpan LeaseDuration { get; }
    public TimeSpan GraceDuration { get; }

    /// <summary>늦게 평가해도 유예 시간을 연장하지 않습니다. 완료 기한과 같은 시각은 이미 만료입니다.</summary>
    public RoomPlayerConnection Evaluate(RoomPlayerConnection state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status == RoomConnectionStatus.Connected && state.LeaseExpiresAt is { } lease && now >= lease)
        {
            if (!CanAdd(lease, GraceDuration)) throw new InvalidOperationException("저장된 연결 만료 시각이 유효 범위를 초과합니다.");
            // 만료된 자격을 보존하지 않습니다. 재접속 경쟁 검사는 ID가 아닌 세대로 수행합니다.
            state = state with { Status = RoomConnectionStatus.Disconnected, ConnectionId = null, DisconnectedAt = lease, ReconnectDeadline = lease + GraceDuration };
        }
        if (state.Status == RoomConnectionStatus.Disconnected && state.ReconnectDeadline is { } deadline && now >= deadline)
        {
            state = state with { Status = RoomConnectionStatus.Abandoned, ConnectionId = null, AbandonedAt = deadline };
        }
        return state;
    }

    /// <summary>최초 연결만 발급합니다. 방 참가 권한·게임 종료 여부·입장 기한은 방 상태 계층에서 먼저 검사합니다.</summary>
    /// <param name="newConnectionId">서버가 새로 생성한 식별자입니다. 클라이언트가 지정하는 값이 아닙니다.</param>
    public RoomConnectionTransition Connect(RoomPlayerConnection state, Guid newConnectionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status != RoomConnectionStatus.AwaitingConnection)
        {
            return Fail(RoomConnectionError.AlreadyConnected, state);
        }
        return Issue(state, newConnectionId, now);
    }

    /// <summary>현재 세대가 예상값과 같은 경우에만 새 연결로 교체합니다. 체력·쿨다운은 이 객체가 소유하지 않습니다.</summary>
    public RoomConnectionTransition Reconnect(RoomPlayerConnection state, long expectedGeneration, Guid newConnectionId, DateTimeOffset now)
    {
        state = Evaluate(state, now);
        if (state.Status is not (RoomConnectionStatus.Connected or RoomConnectionStatus.Disconnected))
        {
            return Fail(RoomConnectionError.ReconnectNotAllowed, state);
        }
        if (state.Generation != expectedGeneration)
        {
            return Fail(RoomConnectionError.StaleConnection, state);
        }
        return Issue(state, newConnectionId, now);
    }

    /// <summary>현재 연결의 생존 신호만 허용합니다. 만료된 연결을 Heartbeat로 부활시키지 않습니다.</summary>
    public RoomConnectionTransition Heartbeat(RoomPlayerConnection state, Guid connectionId, long generation, DateTimeOffset now)
    {
        state = Evaluate(state, now);
        var error = ValidateCurrent(state, connectionId, generation);
        if (error != RoomConnectionError.None) return Fail(error, state);
        if (!CanAdd(now, LeaseDuration + GraceDuration)) return Fail(RoomConnectionError.TimeRangeExceeded, state);
        if (state.LastSeenAt is { } seen && now < seen) return Fail(RoomConnectionError.ServerClockMovedBackwards, state);
        return new(RoomConnectionError.None, state with { LastSeenAt = now, LeaseExpiresAt = now + LeaseDuration });
    }

    /// <summary>정상 종료 요청으로 유예 시간을 시작합니다. 접속 종료와 파티 탈퇴는 별개입니다.</summary>
    public RoomConnectionTransition Disconnect(RoomPlayerConnection state, Guid connectionId, long generation, DateTimeOffset now)
    {
        state = Evaluate(state, now);
        var error = ValidateCurrent(state, connectionId, generation);
        if (error != RoomConnectionError.None) return Fail(error, state);
        if (!CanAdd(now, GraceDuration)) return Fail(RoomConnectionError.TimeRangeExceeded, state);
        if (state.LastSeenAt is { } seen && now < seen) return Fail(RoomConnectionError.ServerClockMovedBackwards, state);
        return new(RoomConnectionError.None, state with
        {
            Status = RoomConnectionStatus.Disconnected,
            ConnectionId = null,
            LeaseExpiresAt = now,
            DisconnectedAt = now,
            ReconnectDeadline = now + GraceDuration,
        });
    }

    /// <summary>과거 연결 발급 응답을 재생해도 되는지 검사합니다. 낡은 자격은 반환하지 않아야 합니다.</summary>
    public static bool CanReplayCredential(RoomPlayerConnection current, RoomPlayerConnection original, DateTimeOffset now)
        => current.Status == RoomConnectionStatus.Connected && current.ConnectionId is not null
            && current.ConnectionId == original.ConnectionId && current.Generation == original.Generation
            && current.LeaseExpiresAt is { } lease && now < lease;

    /// <summary>경기 종료 시 활성 자격을 제거하고, 이미 포기한 참가자의 감사 상태는 보존합니다.</summary>
    public static RoomPlayerConnection Close(RoomPlayerConnection state)
        => state.Status == RoomConnectionStatus.Abandoned
            ? state with { ConnectionId = null }
            : state with { Status = RoomConnectionStatus.Left, ConnectionId = null, LeaseExpiresAt = null };

    private RoomConnectionTransition Issue(RoomPlayerConnection state, Guid id, DateTimeOffset now)
    {
        if (id == Guid.Empty || id == state.ConnectionId) return Fail(RoomConnectionError.InvalidConnectionId, state);
        if (state.Generation < 0) throw new InvalidOperationException("연결 세대는 음수일 수 없습니다.");
        if (state.LastSeenAt is { } seen && now < seen) return Fail(RoomConnectionError.ServerClockMovedBackwards, state);
        if (state.Generation == long.MaxValue) return Fail(RoomConnectionError.GenerationExhausted, state);
        if (!CanAdd(now, LeaseDuration + GraceDuration)) return Fail(RoomConnectionError.TimeRangeExceeded, state);
        return new(RoomConnectionError.None, state with
        {
            Status = RoomConnectionStatus.Connected,
            ConnectionId = id,
            Generation = state.Generation + 1,
            LastSeenAt = now,
            LeaseExpiresAt = now + LeaseDuration,
            DisconnectedAt = null,
            ReconnectDeadline = null,
            AbandonedAt = null,
        });
    }

    private static RoomConnectionError ValidateCurrent(RoomPlayerConnection state, Guid id, long generation)
        => state.Status != RoomConnectionStatus.Connected ? RoomConnectionError.NotConnected
            : id == Guid.Empty || id != state.ConnectionId || generation != state.Generation
                ? RoomConnectionError.StaleConnection : RoomConnectionError.None;

    private static bool CanAdd(DateTimeOffset now, TimeSpan duration)
        => now.Ticks <= DateTimeOffset.MaxValue.Ticks - duration.Ticks
            && now.UtcTicks <= DateTimeOffset.MaxValue.UtcTicks - duration.Ticks;
    private static RoomConnectionTransition Fail(RoomConnectionError error, RoomPlayerConnection state) => new(error, state);
}

/// <summary>한 참가자의 연결 상태입니다. 경기 체력과 독립적인 불변 데이터이며 공용 응답에 자격을 노출하지 않습니다.</summary>
public sealed record RoomPlayerConnection(
    RoomConnectionStatus Status = RoomConnectionStatus.AwaitingConnection,
    Guid? ConnectionId = null,
    long Generation = 0,
    DateTimeOffset? LastSeenAt = null,
    DateTimeOffset? LeaseExpiresAt = null,
    DateTimeOffset? DisconnectedAt = null,
    DateTimeOffset? ReconnectDeadline = null,
    DateTimeOffset? AbandonedAt = null);

/// <summary>거부라도 만료 평가는 반영될 수 있습니다. 방 계층은 반환 후보를 저장한 뒤에만 채택합니다.</summary>
public sealed record RoomConnectionTransition(RoomConnectionError Error, RoomPlayerConnection State);
public enum RoomConnectionStatus { AwaitingConnection, Connected, Disconnected, Abandoned, Left }
public enum RoomConnectionError
{
    None, AlreadyConnected, ReconnectNotAllowed, StaleConnection, NotConnected,
    InvalidConnectionId, GenerationExhausted, TimeRangeExceeded, ServerClockMovedBackwards,
}
