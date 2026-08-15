namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>특정 매칭 조건에서 현재 대기 중인 파티의 순서가 포함된 스냅샷입니다.</summary>
[GenerateSerializer]
public sealed record MatchQueueSnapshot(
    [property: Id(0)] string QueueKey,
    [property: Id(1)] int TargetPlayerCount,
    [property: Id(2)] MatchQueueTicket[] QueuedTickets);
