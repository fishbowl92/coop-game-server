namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>대기열 등록 또는 취소 명령의 처리 결과입니다.</summary>
[GenerateSerializer]
public sealed record MatchQueueCommandResult(
    [property: Id(0)] bool IsReplay,
    [property: Id(1)] MatchQueueCommandError Error,
    [property: Id(2)] MatchQueueTicket? Ticket,
    [property: Id(3)] MatchAssignment? Match);
