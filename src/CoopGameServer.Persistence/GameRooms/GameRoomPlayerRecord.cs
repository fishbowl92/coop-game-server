using CoopGameServer.Domain.GameRooms;

namespace CoopGameServer.Persistence.GameRooms;

/// <summary>game_room_players의 참가자 한 행입니다. 계정의 영구 체력이 아니라 경기별 상태입니다.</summary>
public sealed class GameRoomPlayerRecord
{
    /// <summary>EF Core(Entity Framework Core, 객체·DB 매핑 도구) 복원용 생성자입니다.</summary>
    private GameRoomPlayerRecord() { }

    /// <summary>방·플레이어·순서를 고정하고 후보 상태의 전투 값을 저장합니다.</summary>
    public GameRoomPlayerRecord(Guid roomId, Guid playerId, int playerOrder,
        int maxHealth, int currentHealth, int combatStatus, long lastCommandSequence,
        DateTimeOffset? basicAttackReadyAt, DateTimeOffset? skillReadyAt)
    {
        RoomId = roomId;
        PlayerId = playerId;
        PlayerOrder = playerOrder;
        Update(maxHealth, currentHealth, combatStatus, lastCommandSequence, basicAttackReadyAt, skillReadyAt);
    }

    /// <summary>소유 게임 방 식별자이며 복합 기본 키의 일부입니다.</summary>
    public Guid RoomId { get; private set; }
    /// <summary>참가자 식별자이며 복합 기본 키의 일부입니다.</summary>
    public Guid PlayerId { get; private set; }
    /// <summary>고정 배정 순서입니다. 같은 방에서 중복될 수 없습니다.</summary>
    public int PlayerOrder { get; private set; }
    /// <summary>최대 체력입니다.</summary>
    public int MaxHealth { get; private set; }
    /// <summary>0 이상 최대 체력 이하인 현재 체력입니다.</summary>
    public int CurrentHealth { get; private set; }
    /// <summary>0은 행동 가능, 1은 전투 불능입니다.</summary>
    public int CombatStatus { get; private set; }
    /// <summary>마지막으로 승인된 전투 명령 순번입니다.</summary>
    public long LastCommandSequence { get; private set; }
    /// <summary>일반 공격 재사용 가능 UTC 시각입니다.</summary>
    public DateTimeOffset? BasicAttackReadyAt { get; private set; }
    /// <summary>스킬 재사용 가능 UTC 시각입니다.</summary>
    public DateTimeOffset? SkillReadyAt { get; private set; }

    /// <summary>실제 소켓이 아니라 HTTP 생존 신호로 판단하는 논리적 연결 상태입니다.</summary>
    public RoomConnectionStatus ConnectionStatus { get; private set; }
    /// <summary>현재 유효한 연결만 가지는 ID입니다. 공개 스냅샷에 포함하지 않습니다.</summary>
    public Guid? ConnectionId { get; private set; }
    /// <summary>재접속할 때 증가하는 세대입니다. 이전 클라이언트의 요청을 차단합니다.</summary>
    public long ConnectionGeneration { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset? DisconnectedAt { get; private set; }
    public DateTimeOffset? ReconnectDeadline { get; private set; }
    public DateTimeOffset? AbandonedAt { get; private set; }

    /// <summary>DB 복원 값을 순수 연결 규칙의 불변 입력으로 변환합니다.</summary>
    public RoomPlayerConnection ReadConnection() => new(ConnectionStatus, ConnectionId, ConnectionGeneration,
        LastSeenAt, LeaseExpiresAt, DisconnectedAt, ReconnectDeadline, AbandonedAt);

    /// <summary>검증된 후보를 저장 대상에 복사합니다. 체력·순번·쿨다운은 변경하지 않습니다.</summary>
    public void UpdateConnection(RoomPlayerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ConnectionStatus = connection.Status;
        ConnectionId = connection.ConnectionId;
        ConnectionGeneration = connection.Generation;
        LastSeenAt = connection.LastSeenAt;
        LeaseExpiresAt = connection.LeaseExpiresAt;
        DisconnectedAt = connection.DisconnectedAt;
        ReconnectDeadline = connection.ReconnectDeadline;
        AbandonedAt = connection.AbandonedAt;
    }

    /// <summary>참가자 신원과 순서를 바꾸지 않고 저장할 전투 값만 반영합니다. 범위는 DB CHECK도 검증합니다.</summary>
    public void Update(int maxHealth, int currentHealth, int combatStatus, long lastCommandSequence,
        DateTimeOffset? basicAttackReadyAt, DateTimeOffset? skillReadyAt)
    {
        MaxHealth = maxHealth;
        CurrentHealth = currentHealth;
        CombatStatus = combatStatus;
        LastCommandSequence = lastCommandSequence;
        BasicAttackReadyAt = basicAttackReadyAt;
        SkillReadyAt = skillReadyAt;
    }
}
