namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>매칭 대기열 명령이 거부된 이유입니다.</summary>
[GenerateSerializer]
public enum MatchQueueCommandError
{
    /// <summary>명령이 정상 처리되었습니다.</summary>
    None = 0,

    /// <summary>요청 재시도를 식별할 requestId가 비어 있습니다.</summary>
    InvalidRequestId = 1,

    /// <summary>파티 식별자가 비어 있습니다.</summary>
    InvalidPartyId = 2,

    /// <summary>리더 플레이어 식별자가 비어 있습니다.</summary>
    InvalidLeaderPlayerId = 3,

    /// <summary>멤버 수, 빈 식별자 또는 멤버 중복 규칙을 위반했습니다.</summary>
    InvalidMembers = 4,

    /// <summary>리더가 멤버 목록에 포함되어 있지 않습니다.</summary>
    LeaderNotMember = 5,

    /// <summary>사전 구성 파티와 솔로 참가자 각각에 필요한 식별자·인원 규칙을 위반했습니다.</summary>
    InvalidEntryShape = 6,

    /// <summary>같은 파티가 이미 이 대기열에서 기다리고 있습니다.</summary>
    PartyAlreadyQueued = 7,

    /// <summary>같은 파티가 이미 게임 방을 배정받았습니다.</summary>
    PartyAlreadyMatched = 8,

    /// <summary>멤버 중 한 명 이상이 다른 파티 티켓으로 대기 또는 매칭 중입니다.</summary>
    PlayerAlreadyQueued = 9,

    /// <summary>취소할 파티 티켓을 찾지 못했습니다.</summary>
    TicketNotFound = 10,

    /// <summary>현재 파티 리더가 아닌 사용자가 취소를 요청했습니다.</summary>
    OnlyLeaderCanCancel = 11,

    /// <summary>이미 게임 방이 배정되어 대기 취소 시점을 지났습니다.</summary>
    TicketAlreadyMatched = 12,

    /// <summary>이미 취소된 티켓을 다른 요청으로 다시 취소했습니다.</summary>
    TicketAlreadyCancelled = 13,

    /// <summary>같은 requestId가 이전 요청과 다른 내용에 재사용되었습니다.</summary>
    RequestIdConflict = 14,
}
