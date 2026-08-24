namespace CoopGameServer.GrainContracts.Players;

/// <summary>진행도 조회 시 반환하는 한 인벤토리 항목의 읽기 전용 복사본입니다.</summary>
/// <param name="ItemId">아이템을 구분하는 양의 정수 식별자입니다.</param>
/// <param name="Quantity">현재 보유 수량입니다.</param>
/// <param name="UpdatedAt">수량이 마지막으로 변경된 UTC 시각입니다.</param>
[GenerateSerializer]
public sealed record PlayerInventoryItemSnapshot(
    [property: Id(0)] int ItemId,
    [property: Id(1)] int Quantity,
    [property: Id(2)] DateTimeOffset UpdatedAt);
