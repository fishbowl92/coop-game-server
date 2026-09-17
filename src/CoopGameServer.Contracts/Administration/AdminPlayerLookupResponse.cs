namespace CoopGameServer.Contracts.Administration;

/// <summary>관리 도구가 선택할 플레이어의 최소 식별 정보입니다.</summary>
public sealed record AdminPlayerLookupResponse(
    Guid PlayerId,
    string Nickname,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
