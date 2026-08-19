namespace CoopGameServer.Persistence.GameRooms;

/// <summary>GameRoomGrain의 현재 상태를 PostgreSQL game_rooms 테이블에 저장하는 행 모델입니다.</summary>
public sealed class GameRoomRecord
{
    /// <summary>EF Core 전용 생성자입니다.</summary>
    private GameRoomRecord()
    {
    }

    /// <summary>새 게임 방의 영속 행을 만듭니다.</summary>
    public GameRoomRecord(
        Guid roomId,
        string queueKey,
        int lifecycle,
        Guid[] partyIds,
        Guid[] playerIds,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        RoomId = roomId;
        QueueKey = queueKey;
        Lifecycle = lifecycle;
        PartyIds = partyIds.ToArray();
        PlayerIds = playerIds.ToArray();
        CreatedAt = createdAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    /// <summary>방을 고유하게 식별하고 GameRoomGrain 기본 키로도 사용하는 값입니다.</summary>
    public Guid RoomId { get; private set; }

    /// <summary>게임 모드·난이도 등 매칭 조건을 나타내는 키입니다.</summary>
    public string QueueKey { get; private set; } = string.Empty;

    /// <summary>Ready·InGame·Completed를 정수로 저장한 값입니다.</summary>
    public int Lifecycle { get; private set; }

    /// <summary>게임 종료 뒤에도 유지할 사전 구성 파티 식별자 배열입니다.</summary>
    public Guid[] PartyIds { get; private set; } = [];

    /// <summary>이 방에 배정된 정확히 4명의 플레이어 식별자 배열입니다.</summary>
    public Guid[] PlayerIds { get; private set; } = [];

    /// <summary>매칭이 성립해 방이 생성된 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>게임 시작 UTC 시각이며 Ready 상태에서는 null입니다.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>게임 완료 UTC 시각이며 완료 전에는 null입니다.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>후보 GameRoomState의 최신 스냅샷으로 영속 행을 갱신합니다.</summary>
    public void Update(
        int lifecycle,
        Guid[] partyIds,
        Guid[] playerIds,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        Lifecycle = lifecycle;
        PartyIds = partyIds.ToArray();
        PlayerIds = playerIds.ToArray();
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }
}
