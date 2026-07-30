namespace CoopGameServer.Contracts.Players;

/// <summary>
/// 클라이언트가 기존 플레이어의 닉네임 변경을 요청할 때 보내는 데이터 형식입니다.
/// </summary>
/// <param name="Nickname">변경할 닉네임입니다. 실제 유효성 검증은 Player 도메인 엔티티가 수행합니다.</param>
/// <remarks>
/// 플레이어 식별자는 URL 경로의 playerId로 전달합니다.
/// 요청 본문에는 변경을 허용할 값인 Nickname만 두어, 클라이언트가 Id·생성 시각을 임의로 바꾸지 못하게 합니다.
/// </remarks>
public sealed record UpdatePlayerNicknameRequest(string? Nickname);
