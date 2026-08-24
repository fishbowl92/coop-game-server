namespace CoopGameServer.GrainContracts.Players;

/// <summary>플레이어 진행도를 페이지 단위로 조회하는 조건입니다.</summary>
/// <param name="PageSize">한 번에 읽을 인벤토리 항목 수이며 구현에서 1~100으로 제한합니다.</param>
/// <param name="ContinuationToken">이전 응답 다음 위치를 나타내는 서버 발급 불투명 문자열입니다.</param>
[GenerateSerializer]
public sealed record GetPlayerProgressionPageQuery(
    [property: Id(0)] int PageSize,
    [property: Id(1)] string? ContinuationToken);
