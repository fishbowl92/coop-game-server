namespace CoopGameServer.Contracts.Parties;

/// <summary>
/// 플레이어 한 명을 파티에서 탈퇴시키는 HTTP 요청 본문입니다.
/// </summary>
/// <param name="RequestId">탈퇴 요청의 재전송을 식별하는 멱등성 키입니다.</param>
/// <param name="PlayerId">탈퇴할 플레이어 식별자입니다.</param>
public sealed record LeavePartyRequest(Guid RequestId, Guid PlayerId);
