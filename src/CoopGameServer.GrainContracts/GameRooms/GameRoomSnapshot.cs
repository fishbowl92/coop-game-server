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
/// <param name="Outcome">완료된 경기의 승리·패배·취소 결과이며, 완료 전에는 None입니다.</param>
/// <param name="RewardPolicyVersion">이 방의 보상을 계산할 때 사용할 정책 버전입니다.</param>
/// <param name="CombatRuleVersion">참가자 상태 규칙 버전입니다. 0은 전투 상세 없는 과거 기록입니다.</param>
/// <param name="Players">참가자 상태입니다. 과거 요청 응답에는 없을 수 있으며 버전 0의 값은 실제 전투 이력이 아닙니다.</param>
/// <param name="CurrentWave">현재 웨이브입니다. 시작 전 또는 과거 상세 없음은 0입니다.</param>
/// <param name="MaxWaves">전체 웨이브 수입니다. 0은 웨이브 상세가 없는 과거 기록입니다.</param>
/// <param name="EnemyMaxHealth">현재 웨이브 적의 최대 체력입니다.</param>
/// <param name="EnemyCurrentHealth">현재 웨이브 적의 남은 체력입니다.</param>
/// <param name="StateVersion">성공한 상태 변경마다 증가하는 버전입니다. 과거 요청 응답의 0은 값이 없었다는 뜻입니다.</param>
/// <param name="EnemyAttackSequence">적 반격 순번입니다. 상태 버전과 독립적으로 관리합니다.</param>
[GenerateSerializer]
public sealed record GameRoomSnapshot(
    [property: Id(0)] Guid RoomId,
    [property: Id(1)] string QueueKey,
    [property: Id(2)] GameRoomLifecycle Lifecycle,
    [property: Id(3)] Guid[] PartyIds,
    [property: Id(4)] Guid[] PlayerIds,
    [property: Id(5)] DateTimeOffset CreatedAt,
    [property: Id(6)] DateTimeOffset? StartedAt,
    [property: Id(7)] DateTimeOffset? CompletedAt,
    [property: Id(8)] GameOutcome Outcome,
    [property: Id(9)] int RewardPolicyVersion,
    [property: Id(10)] int CombatRuleVersion = 0,
    [property: Id(11)] PlayerCombatSnapshot[]? Players = null,
    [property: Id(12)] int CurrentWave = 0,
    [property: Id(13)] int MaxWaves = 0,
    [property: Id(14)] int EnemyMaxHealth = 0,
    [property: Id(15)] int EnemyCurrentHealth = 0,
    [property: Id(16)] long StateVersion = 0,
    [property: Id(17)] long EnemyAttackSequence = 0,
    [property: Id(18)] DateTimeOffset? InitialConnectDeadline = null,
    [property: Id(19)] string? CancellationReason = null);
