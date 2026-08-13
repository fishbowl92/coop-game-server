namespace CoopGameServer.Contracts.Parties;

/// <summary>
/// 플레이어 한 명을 기존 파티에 가입시키는 HTTP 요청 본문입니다.
/// </summary>
/// <param name="RequestId">가입 요청의 재전송을 식별하는 멱등성 키입니다.</param>
/// <param name="PlayerId">가입할 플레이어 식별자입니다.</param>
public sealed record JoinPartyRequest(Guid RequestId, Guid PlayerId);
