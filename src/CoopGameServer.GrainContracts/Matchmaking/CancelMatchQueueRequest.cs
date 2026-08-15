namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>대기 중인 파티의 매칭을 취소하기 위한 내부 요청입니다.</summary>
[GenerateSerializer]
public sealed record CancelMatchQueueRequest(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] Guid TicketId,
    [property: Id(2)] Guid RequesterPlayerId);
