namespace CoopGameServer.Contracts.Players;

/// <summary>프로필·골드·인벤토리 한 페이지를 묶은 플레이어 진행도 응답입니다.</summary>
public sealed record PlayerProgressionResponse(
    Guid PlayerId,
    string Nickname,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Gold,
    PlayerInventoryItemResponse[] Items,
    string? NextContinuationToken);
