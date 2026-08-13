namespace CoopGameServer.Contracts.Parties;

/// <summary>
/// 현재 리더가 파티를 명시적으로 해산할 때 전달하는 HTTP 요청 본문입니다.
/// </summary>
/// <param name="RequestId">해산 요청의 재전송을 식별하는 멱등성 키입니다.</param>
/// <param name="LeaderPlayerId">해산 권한을 확인할 현재 리더의 플레이어 식별자입니다.</param>
public sealed record DisbandPartyRequest(Guid RequestId, Guid LeaderPlayerId);
