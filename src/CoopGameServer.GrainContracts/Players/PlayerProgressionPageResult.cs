namespace CoopGameServer.GrainContracts.Players;

/// <summary>플레이어 프로필, 골드와 인벤토리 한 페이지를 담는 조회 결과입니다.</summary>
/// <remarks>
/// 기존 Orleans 직렬화 호환성을 위해 필드 번호 0~3은 유지하고 새 프로필 필드는 4번부터 추가합니다.
/// 오류 결과에서도 Items는 null이 아닌 빈 배열을 사용합니다.
/// </remarks>
[GenerateSerializer]
public sealed record PlayerProgressionPageResult(
    [property: Id(0)] PlayerProgressionQueryError Error,
    [property: Id(1)] long Gold,
    [property: Id(2)] PlayerInventoryItemSnapshot[] Items,
    [property: Id(3)] string? NextContinuationToken,
    [property: Id(4)] Guid PlayerId,
    [property: Id(5)] string? Nickname,
    [property: Id(6)] DateTimeOffset CreatedAt,
    [property: Id(7)] DateTimeOffset UpdatedAt);
