namespace CoopGameServer.GrainContracts.Players;

/// <summary>플레이어의 골드와 인벤토리 한 페이지를 담는 조회 결과입니다.</summary>
/// <param name="Error">조회 실패 이유이며 성공하면 None입니다.</param>
/// <param name="Gold">PostgreSQL에 저장된 현재 골드입니다.</param>
/// <param name="Items">ItemId 오름차순으로 정렬된 최대 100개의 인벤토리 항목입니다.</param>
/// <param name="NextContinuationToken">다음 페이지가 있을 때만 반환하는 서버 발급 연속 토큰입니다.</param>
/// <remarks>
/// 오류 결과에서도 Items는 null이 아니라 빈 배열을 사용해 호출자의 null 처리를 단순하게 합니다.
/// </remarks>
[GenerateSerializer]
public sealed record PlayerProgressionPageResult(
    [property: Id(0)] PlayerProgressionQueryError Error,
    [property: Id(1)] long Gold,
    [property: Id(2)] PlayerInventoryItemSnapshot[] Items,
    [property: Id(3)] string? NextContinuationToken);
