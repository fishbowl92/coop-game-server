namespace CoopGameServer.GrainContracts.Players;

/// <summary>PostgreSQL에 확정된 보상 감사 기록을 Orleans 호출자에게 전달하는 영수증입니다.</summary>
/// <param name="RewardAuditId">보상 감사 기록의 고유 식별자입니다.</param>
/// <param name="RequestId">최초 보상 요청의 멱등성 키입니다.</param>
/// <param name="PlayerId">보상을 받은 플레이어 식별자입니다.</param>
/// <param name="GoldAmount">실제로 반영된 골드 수량입니다.</param>
/// <param name="ItemId">실제로 반영된 아이템 식별자이며 없으면 null입니다.</param>
/// <param name="ItemQuantity">실제로 반영된 아이템 수량이며 없으면 null입니다.</param>
/// <param name="Reason">서버가 기록한 보상 지급 사유입니다.</param>
/// <param name="CreatedAt">보상이 확정된 UTC(Coordinated Universal Time, 협정 세계시) 시각입니다.</param>
[GenerateSerializer]
public sealed record PlayerRewardReceipt(
    [property: Id(0)] Guid RewardAuditId,
    [property: Id(1)] Guid RequestId,
    [property: Id(2)] Guid PlayerId,
    [property: Id(3)] long GoldAmount,
    [property: Id(4)] int? ItemId,
    [property: Id(5)] int? ItemQuantity,
    [property: Id(6)] string Reason,
    [property: Id(7)] DateTimeOffset CreatedAt);
