namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>파티가 매칭 대기열에서 가진 현재 상태를 나타내는 읽기 전용 스냅샷입니다.</summary>
[GenerateSerializer]
public sealed record MatchQueueTicket(
    [property: Id(0)] Guid TicketId,
    [property: Id(1)] string QueueKey,
    [property: Id(2)] MatchQueueEntryKind EntryKind,
    [property: Id(3)] Guid? PartyId,
    [property: Id(4)] Guid LeaderPlayerId,
    [property: Id(5)] Guid[] MemberPlayerIds,
    [property: Id(6)] MatchQueueTicketStatus Status,
    [property: Id(7)] Guid? RoomId,
    [property: Id(8)] DateTimeOffset EnqueuedAt,
    [property: Id(9)] long QueueOrder);
