namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>
/// 검증이 끝난 사전 구성 파티 또는 솔로 참가자를 매칭 대기열에 넣기 위한 내부 요청입니다.
/// </summary>
/// <remarks>
/// 향후 HTTP API는 클라이언트가 멤버 목록을 직접 조작하지 못하도록 합니다.
/// Application 계층이 사전 구성 파티라면 PartyGrain의 최신 스냅샷을 읽고,
/// 솔로라면 인증된 플레이어 한 명만 사용해 이 내부 요청을 만들어야 합니다.
/// </remarks>
[GenerateSerializer]
public sealed record MatchQueueEntryRequest(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] MatchQueueEntryKind EntryKind,
    [property: Id(2)] Guid? PartyId,
    [property: Id(3)] Guid LeaderPlayerId,
    [property: Id(4)] Guid[] MemberPlayerIds);
