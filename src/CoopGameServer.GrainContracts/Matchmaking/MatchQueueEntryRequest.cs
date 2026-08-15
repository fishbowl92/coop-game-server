namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>
/// 검증이 끝난 파티를 매칭 대기열에 넣기 위한 내부 요청입니다.
/// </summary>
/// <remarks>
/// 향후 HTTP API는 클라이언트가 멤버 목록을 직접 조작하지 못하도록 partyId만 받습니다.
/// Application 계층이 PartyGrain에서 최신 스냅샷을 읽고 이 요청을 만들어야 합니다.
/// </remarks>
[GenerateSerializer]
public sealed record MatchQueueEntryRequest(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] Guid PartyId,
    [property: Id(2)] Guid LeaderPlayerId,
    [property: Id(3)] Guid[] MemberPlayerIds);
