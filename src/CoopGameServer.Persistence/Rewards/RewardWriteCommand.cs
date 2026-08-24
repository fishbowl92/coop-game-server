namespace CoopGameServer.Persistence.Rewards;

/// <summary>PostgreSQL 보상 쓰기에 필요한 최소 입력입니다.</summary>
/// <param name="RequestId">같은 요청을 한 번만 적용하기 위한 전역 멱등성 키입니다.</param>
/// <param name="PlayerId">보상을 받을 플레이어 식별자입니다.</param>
/// <param name="GoldAmount">추가할 골드 수량입니다.</param>
/// <param name="ItemId">추가할 아이템 식별자이며 아이템 보상이 없으면 null입니다.</param>
/// <param name="ItemQuantity">추가할 아이템 수량이며 아이템 보상이 없으면 null입니다.</param>
/// <param name="Reason">감사 이력에 남길 서버 측 지급 사유입니다.</param>
/// <remarks>
/// 이 형식은 HTTP DTO(Data Transfer Object, 계층 사이에서 데이터를 운반하는 객체)나
/// Orleans 계약을 참조하지 않아 API와 Silo 양쪽에서 재사용할 수 있습니다.
/// </remarks>
public sealed record RewardWriteCommand(
    Guid RequestId,
    Guid PlayerId,
    long GoldAmount,
    int? ItemId,
    int? ItemQuantity,
    string Reason);
