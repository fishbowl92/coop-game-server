namespace CoopGameServer.Persistence.Rewards;

/// <summary>PostgreSQL에 확정된 보상 감사 기록의 변경 불가능한 복사본입니다.</summary>
/// <param name="RewardAuditId">보상 감사 기록의 기본 키입니다.</param>
/// <param name="RequestId">보상 요청의 멱등성 키입니다.</param>
/// <param name="PlayerId">보상을 받은 플레이어 식별자입니다.</param>
/// <param name="GoldAmount">실제로 반영된 골드 수량입니다.</param>
/// <param name="ItemId">실제로 반영된 아이템 식별자이며 없으면 null입니다.</param>
/// <param name="ItemQuantity">실제로 반영된 아이템 수량이며 없으면 null입니다.</param>
/// <param name="Reason">정규화되어 저장된 보상 지급 사유입니다.</param>
/// <param name="CreatedAt">보상이 확정된 UTC(Coordinated Universal Time, 협정 세계시) 시각입니다.</param>
public sealed record RewardWriteReceipt(
    Guid RewardAuditId,
    Guid RequestId,
    Guid PlayerId,
    long GoldAmount,
    int? ItemId,
    int? ItemQuantity,
    string Reason,
    DateTimeOffset CreatedAt);
