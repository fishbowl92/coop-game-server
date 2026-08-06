namespace CoopGameServer.Contracts.Rewards;

/// <summary>
/// 보상 지급 요청의 처리 결과를 나타내는 API 응답 형식입니다.
/// </summary>
/// <param name="RewardAuditId">보상 이력 자체의 고유 식별자입니다.</param>
/// <param name="RequestId">처리한 멱등성 키입니다.</param>
/// <param name="PlayerId">보상을 받은 플레이어 식별자입니다.</param>
/// <param name="GoldAmount">지급된 골드 양입니다.</param>
/// <param name="ItemId">지급된 아이템 종류이며, 아이템이 없으면 null입니다.</param>
/// <param name="ItemQuantity">지급된 아이템 수량이며, 아이템이 없으면 null입니다.</param>
/// <param name="Reason">저장된 보상 사유입니다.</param>
/// <param name="CreatedAt">보상이 최초 적용된 UTC(Coordinated Universal Time, 협정 세계시) 기준 시각입니다.</param>
/// <param name="IsReplay">
/// false면 이번 요청이 새 보상을 적용했다는 뜻이고,
/// true면 같은 RequestId의 기존 결과를 재전송했다는 뜻입니다.
/// </param>
public sealed record GrantRewardResponse(
    Guid RewardAuditId,
    Guid RequestId,
    Guid PlayerId,
    long GoldAmount,
    int? ItemId,
    int? ItemQuantity,
    string Reason,
    DateTimeOffset CreatedAt,
    bool IsReplay);
