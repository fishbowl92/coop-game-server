namespace CoopGameServer.GrainContracts.Parties;

/// <summary>
/// 파티 상태 변경 명령이 적용되지 않은 업무상 이유입니다.
/// </summary>
/// <remarks>
/// 예외 대신 직렬화 가능한 값을 반환하면 후속 HTTP API가 400, 404, 409 같은 상태 코드로
/// 명시적으로 변환할 수 있습니다.
/// </remarks>
public enum PartyCommandError
{
    /// <summary>명령이 정상적으로 적용되었습니다.</summary>
    None = 0,

    /// <summary>partyId가 비어 있습니다.</summary>
    InvalidPartyId = 1,

    /// <summary>requestId가 비어 있습니다.</summary>
    InvalidRequestId = 2,

    /// <summary>플레이어 식별자가 비어 있습니다.</summary>
    InvalidPlayerId = 3,

    /// <summary>players 테이블에 존재하지 않는 플레이어입니다.</summary>
    PlayerNotFound = 4,

    /// <summary>이미 활성 상태인 파티입니다.</summary>
    PartyAlreadyExists = 5,

    /// <summary>한 번 해산된 partyId를 다시 사용하려 했습니다.</summary>
    PartyIdCannotBeReused = 6,

    /// <summary>아직 생성되지 않은 파티입니다.</summary>
    PartyNotCreated = 7,

    /// <summary>이미 해산된 파티입니다.</summary>
    PartyDisbanded = 8,

    /// <summary>파티 정원 네 명이 모두 찼습니다.</summary>
    PartyFull = 9,

    /// <summary>이미 가입한 플레이어입니다.</summary>
    MemberAlreadyJoined = 10,

    /// <summary>다른 활성 파티에 이미 가입한 플레이어입니다.</summary>
    PlayerAlreadyInAnotherParty = 11,

    /// <summary>파티에 가입하지 않은 플레이어입니다.</summary>
    MemberNotFound = 12,

    /// <summary>현재 리더가 아닌 플레이어가 명시적 해산을 요청했습니다.</summary>
    OnlyLeaderCanDisband = 13,

    /// <summary>이미 사용한 requestId를 다른 명령 내용에 재사용했습니다.</summary>
    RequestIdConflict = 14,
}
