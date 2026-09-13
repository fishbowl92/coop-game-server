namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>서버가 인증한 참가자의 공격 의도입니다. 피해량·보상은 클라이언트가 지정하지 않습니다.</summary>
[GenerateSerializer]
public sealed record GameRoomCombatCommand(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] Guid PlayerId,
    [property: Id(2)] long Sequence,
    [property: Id(3)] CombatActionKind Kind,
    [property: Id(4)] long? KnownStateVersion = null,
    [property: Id(5)] Guid ConnectionId = default,
    [property: Id(6)] long ConnectionGeneration = 0);

/// <summary>최초 응답 재생 여부, 오류, 후보 상태와 재시도 가능 시각입니다.</summary>
[GenerateSerializer]
public sealed record GameRoomCombatResult(
    [property: Id(0)] GameRoomCombatError Error,
    [property: Id(1)] bool IsReplay,
    [property: Id(2)] GameRoomSnapshot? Room,
    [property: Id(3)] DateTimeOffset? RetryAt);

/// <summary>서버가 지원하는 두 전투 행동입니다.</summary>
public enum CombatActionKind { BasicAttack, UseSkill }

/// <summary>전투 요청 거부 사유입니다. 숫자 순서는 기존 저장 결과와의 호환성을 위해 유지합니다.</summary>
public enum GameRoomCombatError
{
    None, InvalidCommand, RoomNotCreated, PlayerNotInRoom, RequestIdConflict, RoomNotInGame, RoomCompleted,
    UnsupportedCombatState, PlayerIncapacitated, SequenceExhausted, SequenceAlreadyPassed, SequenceGap,
    InvalidKnownStateVersion, CooldownActive, StateVersionExhausted, EnemySequenceExhausted, TimeRangeExceeded,
    StaleConnection,
}
