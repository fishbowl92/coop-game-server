namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>API가 인증한 플레이어로 구성하는 내부 연결 명령입니다. 새 연결 ID는 서버가 발급합니다.</summary>
[GenerateSerializer]
public sealed record GameRoomConnectionCommand(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] Guid PlayerId,
    [property: Id(2)] GameRoomConnectionAction Action,
    [property: Id(3)] Guid ConnectionId = default,
    [property: Id(4)] long Generation = 0);

public enum GameRoomConnectionAction { Connect, Reconnect, Heartbeat, Disconnect, StartCombat }

/// <summary>연결 자격은 요청자 자신의 값만 포함합니다. 타인의 연결 ID는 반환하지 않습니다.</summary>
[GenerateSerializer]
public sealed record GameRoomConnectionResult(
    [property: Id(0)] string Error,
    [property: Id(1)] bool IsReplay,
    [property: Id(2)] GameRoomSnapshot? Room,
    [property: Id(3)] Guid? ConnectionId = null,
    [property: Id(4)] long Generation = 0,
    [property: Id(5)] DateTimeOffset? LeaseExpiresAt = null);
