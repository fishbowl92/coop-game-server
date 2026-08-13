namespace CoopGameServer.Persistence.Parties;

/// <summary>
/// PostgreSQL의 parties 테이블에 저장되는 파티의 현재 상태입니다.
/// </summary>
/// <remarks>
/// 이 형식은 게임 규칙을 판단하는 도메인 객체가 아니라 EF Core(Entity Framework Core, 엔티티 프레임워크 코어)가
/// 데이터베이스 행을 읽고 쓰기 위해 사용하는 영속성 전용 모델입니다.
/// </remarks>
public sealed class PartyRecord
{
    private PartyRecord()
    {
    }

    /// <summary>
    /// 새 파티 저장 행을 만듭니다.
    /// </summary>
    public PartyRecord(
        Guid partyId,
        int lifecycle,
        Guid? leaderPlayerId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        PartyId = partyId;
        Lifecycle = lifecycle;
        LeaderPlayerId = leaderPlayerId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Orleans PartyGrain의 기본 키와 같은 파티 식별자입니다.</summary>
    public Guid PartyId { get; private set; }

    /// <summary>PartyLifecycle 열거형 값을 정수로 저장한 생명 주기 상태입니다.</summary>
    public int Lifecycle { get; private set; }

    /// <summary>현재 리더의 플레이어 식별자이며, 해산된 파티에서는 null입니다.</summary>
    public Guid? LeaderPlayerId { get; private set; }

    /// <summary>파티가 최초 생성된 UTC(Coordinated Universal Time, 협정 세계시) 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>파티 상태가 마지막으로 변경된 UTC 시각입니다.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// 파티의 현재 생명 주기와 리더를 최신 상태로 갱신합니다.
    /// </summary>
    public void Update(int lifecycle, Guid? leaderPlayerId, DateTimeOffset updatedAt)
    {
        Lifecycle = lifecycle;
        LeaderPlayerId = leaderPlayerId;
        UpdatedAt = updatedAt;
    }
}
