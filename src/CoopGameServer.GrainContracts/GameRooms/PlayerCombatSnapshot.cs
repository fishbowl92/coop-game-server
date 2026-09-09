namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>한 참가자의 전투 상태입니다. 연결 자격 정보는 포함하지 않습니다.</summary>
/// <param name="PlayerId">게임 방 참가자 식별자입니다.</param>
/// <param name="PlayerOrder">배정 순서입니다. 0~3이며 이후 적의 순차 공격 대상 선택에 사용합니다.</param>
/// <param name="MaxHealth">최대 체력입니다.</param>
/// <param name="CurrentHealth">현재 체력입니다.</param>
/// <param name="CombatStatus">행동 가능 또는 전투 불능 상태입니다.</param>
/// <param name="LastAcceptedCommandSequence">마지막으로 성공한 전투 명령 순번입니다.</param>
/// <param name="BasicAttackReadyAt">일반 공격을 다시 허용할 서버 시각입니다.</param>
/// <param name="SkillReadyAt">스킬을 다시 허용할 서버 시각입니다.</param>
[GenerateSerializer]
public sealed record PlayerCombatSnapshot(
    [property: Id(0)] Guid PlayerId,
    [property: Id(1)] int PlayerOrder,
    [property: Id(2)] int MaxHealth,
    [property: Id(3)] int CurrentHealth,
    [property: Id(4)] PlayerCombatStatus CombatStatus,
    [property: Id(5)] long LastAcceptedCommandSequence,
    [property: Id(6)] DateTimeOffset? BasicAttackReadyAt,
    [property: Id(7)] DateTimeOffset? SkillReadyAt);

/// <summary>체력에 따른 전투 가능 여부입니다. 접속 상태와는 별개입니다.</summary>
public enum PlayerCombatStatus
{
    /// <summary>체력이 남아 있어 전투할 수 있습니다.</summary>
    Active = 0,

    /// <summary>체력이 0이 되어 전투할 수 없습니다.</summary>
    Incapacitated = 1,
}
