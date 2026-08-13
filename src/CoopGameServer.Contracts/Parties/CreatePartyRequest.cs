namespace CoopGameServer.Contracts.Parties;

/// <summary>
/// 새 파티를 생성할 때 전달하는 HTTP 요청 본문입니다.
/// </summary>
/// <param name="RequestId">
/// 같은 생성 요청의 재전송을 식별하는 멱등성 키입니다.
/// 최초 요청과 재시도 요청은 반드시 같은 값을 사용해야 합니다.
/// </param>
/// <param name="LeaderPlayerId">파티를 생성하고 첫 리더가 될 플레이어 식별자입니다.</param>
public sealed record CreatePartyRequest(Guid RequestId, Guid LeaderPlayerId);
