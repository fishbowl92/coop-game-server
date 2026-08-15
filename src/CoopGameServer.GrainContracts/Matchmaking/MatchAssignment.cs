namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>정확히 4명이 모였을 때 만들어지는 임시 게임 방 배정 결과입니다.</summary>
[GenerateSerializer]
public sealed record MatchAssignment(
    [property: Id(0)] Guid RoomId,
    [property: Id(1)] string QueueKey,
    [property: Id(2)] Guid[] PartyIds,
    [property: Id(3)] Guid[] PlayerIds,
    [property: Id(4)] DateTimeOffset CreatedAt);
