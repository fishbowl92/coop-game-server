namespace CoopGameServer.GrainContracts.Players;

/// <summary>신뢰된 관리자 API가 PlayerGrain에 전달하는 보상 명령입니다.</summary>
/// <param name="RequestId">같은 요청의 재전송을 식별하는 멱등성 키입니다.</param>
/// <param name="GoldAmount">추가할 골드 수량입니다.</param>
/// <param name="ItemId">추가할 아이템 식별자이며 아이템 보상이 없으면 null입니다.</param>
/// <param name="ItemQuantity">추가할 아이템 수량이며 아이템 보상이 없으면 null입니다.</param>
/// <param name="Reason">보상을 지급한 서버 측 사유입니다.</param>
/// <remarks>
/// Player 식별자는 이 명령에 넣지 않습니다. 호출 대상 PlayerGrain의 Guid 기본 키가
/// 유일한 playerId 원본이 됩니다.
/// </remarks>
[GenerateSerializer]
public sealed record GrantPlayerRewardCommand(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] long GoldAmount,
    [property: Id(2)] int? ItemId,
    [property: Id(3)] int? ItemQuantity,
    [property: Id(4)] string Reason);
