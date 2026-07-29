namespace CoopGameServer.Contracts.Players;

/// <summary>
/// 클라이언트가 플레이어 생성을 요청할 때 보내는 데이터 형식입니다.
/// </summary>
/// <param name="Nickname">게임 화면에 표시할 플레이어 닉네임입니다.</param>
/// <remarks>
/// 이 계약(Contract)은 API와 향후 Unity 클라이언트가 같은 요청 구조를 이해하도록 분리합니다.
/// 실제 닉네임 규칙 검증은 서버의 Player 도메인 엔티티가 최종 책임집니다.
/// </remarks>
public sealed record CreatePlayerRequest(string? Nickname);
