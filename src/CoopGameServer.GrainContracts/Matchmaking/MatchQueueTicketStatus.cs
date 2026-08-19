namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>한 파티의 매칭 티켓 처리 상태입니다.</summary>
[GenerateSerializer]
public enum MatchQueueTicketStatus
{
    /// <summary>다른 파티를 기다리고 있습니다.</summary>
    Queued = 0,

    /// <summary>정확히 4명이 모여 게임 방을 배정받았습니다.</summary>
    Matched = 1,

    /// <summary>리더가 매칭 전에 대기를 취소했습니다.</summary>
    Cancelled = 2,

    /// <summary>배정된 게임 방이 종료되어 참가자가 다음 매칭을 신청할 수 있습니다.</summary>
    Completed = 3,
}
