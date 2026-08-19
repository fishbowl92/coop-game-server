namespace CoopGameServer.Contracts.GameRooms;

/// <summary>매칭으로 생성된 게임 방의 현재 생명주기와 참가자 구성입니다.</summary>
public sealed record GameRoomResponse(
    Guid RoomId,
    string QueueKey,
    string Lifecycle,
    Guid[] PartyIds,
    Guid[] PlayerIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    bool IsReplay);
