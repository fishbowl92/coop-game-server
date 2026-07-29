namespace CoopGameServer.Contracts.Players;

/// <summary>
/// 서버가 클라이언트에 플레이어 정보를 돌려줄 때 사용하는 응답 형식입니다.
/// </summary>
/// <param name="Id">서버가 생성한 플레이어의 고유 식별자입니다.</param>
/// <param name="Nickname">정규화(앞뒤 공백 제거)된 플레이어 닉네임입니다.</param>
/// <param name="CreatedAt">플레이어 생성 시각이며 UTC 기준입니다.</param>
/// <param name="UpdatedAt">플레이어 정보의 마지막 수정 시각이며 UTC 기준입니다.</param>
public sealed record PlayerResponse(
    Guid Id,
    string Nickname,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
