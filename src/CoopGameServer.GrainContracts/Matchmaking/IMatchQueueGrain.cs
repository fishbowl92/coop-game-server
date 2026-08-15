namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>
/// 같은 매칭 조건에 들어온 파티를 보관하고, 정확히 4명이 모이면 임시 게임 방을 배정하는 Orleans Grain 계약입니다.
/// </summary>
/// <remarks>
/// Grain의 문자열 기본 키는 매칭 조건을 나타냅니다. 예: <c>coop-dungeon-normal-v1</c>.
/// 서로 다른 키를 사용하면 난이도나 게임 모드별 대기열이 자연스럽게 분리됩니다.
/// </remarks>
public interface IMatchQueueGrain : IGrainWithStringKey
{
    /// <summary>파티 전체를 하나의 단위로 대기열에 등록합니다.</summary>
    Task<MatchQueueCommandResult> EnqueueAsync(MatchQueueEntryRequest request);

    /// <summary>아직 매칭되지 않은 파티의 대기를 취소합니다.</summary>
    Task<MatchQueueCommandResult> CancelAsync(CancelMatchQueueRequest request);

    /// <summary>특정 대기 티켓의 현재 상태를 조회합니다.</summary>
    Task<MatchQueueTicket?> GetTicketAsync(Guid ticketId);

    /// <summary>현재 매칭 조건에서 대기 중인 파티를 등록 순서대로 조회합니다.</summary>
    Task<MatchQueueSnapshot> GetSnapshotAsync();
}
