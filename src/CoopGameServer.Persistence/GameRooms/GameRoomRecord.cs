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
        DateTimeOffset? completedAt,
        int outcome,
        int rewardPolicyVersion,
        int combatRuleVersion = 0,
        int currentWave = 0,
        int maxWaves = 0,
        int enemyMaxHealth = 0,
        int enemyCurrentHealth = 0,
        long stateVersion = 1,
        long enemyAttackSequence = 0)
    {
        RoomId = roomId;
        QueueKey = queueKey;
        Lifecycle = lifecycle;
        PartyIds = partyIds.ToArray();
        PlayerIds = playerIds.ToArray();
        CreatedAt = createdAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Outcome = outcome;
        RewardPolicyVersion = rewardPolicyVersion;
        CombatRuleVersion = combatRuleVersion;
        UpdateCombatProgress(currentWave, maxWaves, enemyMaxHealth, enemyCurrentHealth, stateVersion, enemyAttackSequence);
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

    /// <summary>None·Victory·Defeat·Cancelled를 정수로 저장한 최종 경기 결과입니다.</summary>
    public int Outcome { get; private set; }

    /// <summary>이 방의 보상 계산에 사용하도록 생성 시점에 고정한 정책 버전입니다.</summary>
    public int RewardPolicyVersion { get; private set; }

    /// <summary>방 생성 시 고정한 참가자 상태 규칙 버전입니다. 0은 전투 상세 없는 과거 완료 방입니다.</summary>
    public int CombatRuleVersion { get; private set; }

    /// <summary>현재 웨이브 번호입니다. 시작 전 또는 과거 상세 없음은 0입니다.</summary>
    public int CurrentWave { get; private set; }
    /// <summary>전체 웨이브 수입니다. 0은 과거 완료 방의 상세 없음 표식입니다.</summary>
    public int MaxWaves { get; private set; }
    /// <summary>현재 적의 최대 체력입니다.</summary>
    public int EnemyMaxHealth { get; private set; }
    /// <summary>현재 적의 남은 체력입니다.</summary>
    public int EnemyCurrentHealth { get; private set; }
    /// <summary>상태 변경마다 증가하는 양수 버전입니다.</summary>
    public long StateVersion { get; private set; }
    /// <summary>실제 적 반격 횟수입니다. 일반 상태 변경으로 증가시키지 않습니다.</summary>
    public long EnemyAttackSequence { get; private set; }

    /// <summary>후보 상태의 전투 진행 값만 반영합니다. 규칙·보상 버전과 과거 요청 결과는 변경하지 않습니다.</summary>
    /// <param name="currentWave">현재 웨이브입니다.</param>
    /// <param name="maxWaves">전체 웨이브 수입니다.</param>
    /// <param name="enemyMaxHealth">현재 적 최대 체력입니다.</param>
    /// <param name="enemyCurrentHealth">현재 적 남은 체력입니다.</param>
    /// <param name="stateVersion">후보 상태의 버전입니다.</param>
    /// <param name="enemyAttackSequence">후보 상태의 반격 순번입니다.</param>
    public void UpdateCombatProgress(int currentWave, int maxWaves, int enemyMaxHealth,
        int enemyCurrentHealth, long stateVersion, long enemyAttackSequence)
    {
        CurrentWave = currentWave;
        MaxWaves = maxWaves;
        EnemyMaxHealth = enemyMaxHealth;
        EnemyCurrentHealth = enemyCurrentHealth;
        StateVersion = stateVersion;
        EnemyAttackSequence = enemyAttackSequence;
    }

    /// <summary>후보 GameRoomState의 최신 스냅샷으로 영속 행을 갱신합니다.</summary>
    public void Update(
        int lifecycle,
        Guid[] partyIds,
        Guid[] playerIds,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        int outcome)
    {
        Lifecycle = lifecycle;
        PartyIds = partyIds.ToArray();
        PlayerIds = playerIds.ToArray();
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Outcome = outcome;

        // RewardPolicyVersion은 방 생성 시점의 계약이므로 Update에서 변경하지 않습니다.
        // 서버의 최신 정책 번호가 바뀌어도 이미 진행된 방은 생성 당시 정책을 계속 사용해야 합니다.
    }
}
