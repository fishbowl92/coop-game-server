namespace CoopGameServer.Contracts.Matchmaking;

/// <summary>아직 성립하지 않은 매칭 티켓을 취소하는 외부 HTTP 요청입니다.</summary>
/// <param name="RequestId">같은 취소 요청의 재전송을 식별하는 멱등성 요청 식별자입니다.</param>
public sealed record CancelMatchRequest(Guid RequestId);
