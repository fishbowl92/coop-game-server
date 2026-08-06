namespace CoopGameServer.Contracts.Rewards;

/// <summary>
/// 플레이어에게 골드와 선택적 아이템을 지급해 달라고 요청하는 API 형식입니다.
/// </summary>
/// <param name="RequestId">
/// 멱등성 키(Idempotency Key, 같은 요청을 여러 번 받아도 보상을 한 번만 적용하기 위한 고유 키)입니다.
/// 클라이언트는 전송 재시도 시에도 반드시 같은 값을 사용해야 합니다.
/// </param>
/// <param name="GoldAmount">지급할 골드입니다. 아이템 보상이 있으면 0일 수 있습니다.</param>
/// <param name="ItemId">지급할 아이템 종류입니다. 아이템을 지급하지 않으면 null입니다.</param>
/// <param name="ItemQuantity">지급할 아이템 수량입니다. 아이템을 지급하지 않으면 null입니다.</param>
/// <param name="Reason">보상을 지급하는 업무상 사유입니다.</param>
public sealed record GrantRewardRequest(
    Guid? RequestId,
    long GoldAmount,
    int? ItemId,
    int? ItemQuantity,
    string? Reason);
