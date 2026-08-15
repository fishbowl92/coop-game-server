namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>파티가 매칭 대기열에서 가진 현재 상태를 나타내는 읽기 전용 스냅샷입니다.</summary>
[GenerateSerializer]
public sealed record MatchQueueTicket(
    [property: Id(0)] Guid TicketId,
    [property: Id(1)] string QueueKey,
    [property: Id(2)] Guid PartyId,
    [property: Id(3)] Guid LeaderPlayerId,
    [property: Id(4)] Guid[] MemberPlayerIds,
    [property: Id(5)] MatchQueueTicketStatus Status,
    [property: Id(6)] Guid? RoomId,
    [property: Id(7)] DateTimeOffset EnqueuedAt);
