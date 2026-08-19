namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>호출 시점에 복사한 게임 방의 읽기 전용 상태입니다.</summary>
/// <param name="RoomId">MatchQueueGrain이 발급하고 Grain 기본 키로 사용하는 방 식별자입니다.</param>
/// <param name="QueueKey">게임 모드·난이도 등 같은 매칭 조건을 나타내는 문자열입니다.</param>
/// <param name="Lifecycle">준비·게임 중·완료 상태입니다.</param>
/// <param name="PartyIds">게임 종료 후에도 유지할 사전 구성 파티 식별자입니다. 솔로 참가자는 포함하지 않습니다.</param>
/// <param name="PlayerIds">이 게임에 배정된 정확히 4명의 플레이어 식별자입니다.</param>
/// <param name="CreatedAt">매칭이 성립해 방이 생성된 UTC 시각입니다.</param>
/// <param name="StartedAt">게임이 시작된 UTC 시각이며, 시작 전에는 null입니다.</param>
/// <param name="CompletedAt">게임이 완료된 UTC 시각이며, 완료 전에는 null입니다.</param>
[GenerateSerializer]
public sealed record GameRoomSnapshot(
    [property: Id(0)] Guid RoomId,
    [property: Id(1)] string QueueKey,
    [property: Id(2)] GameRoomLifecycle Lifecycle,
    [property: Id(3)] Guid[] PartyIds,
    [property: Id(4)] Guid[] PlayerIds,
    [property: Id(5)] DateTimeOffset CreatedAt,
    [property: Id(6)] DateTimeOffset? StartedAt,
    [property: Id(7)] DateTimeOffset? CompletedAt);
