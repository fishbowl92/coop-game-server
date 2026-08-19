namespace CoopGameServer.Contracts.Matchmaking;

/// <summary>
/// 솔로 플레이어 또는 사전 구성 파티를 매칭 대기열에 등록하는 외부 HTTP 요청입니다.
/// </summary>
/// <param name="RequestId">
/// 네트워크 재전송에도 같은 등록 결과를 돌려받기 위한 멱등성 요청 식별자입니다.
/// 플레이어·파티 식별자는 JWT와 PartyGrain에서 서버가 직접 확인하므로 본문에 받지 않습니다.
/// </param>
public sealed record EnqueueMatchRequest(Guid RequestId);
