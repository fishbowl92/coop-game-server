namespace CoopGameServer.Contracts.Matchmaking;

/// <summary>
/// 외부 클라이언트가 선택할 수 있는 서버 정의 매칭 대기열 키입니다.
/// </summary>
/// <remarks>
/// 현재는 게임 모드가 하나이므로 단일 Queue만 공개합니다.
/// 여러 Queue를 공개하기 전에는 한 플레이어가 서로 다른 Queue에 동시에 들어가지 못하도록
/// 플레이어 전역 매칭 예약을 먼저 구현해야 합니다.
/// </remarks>
public static class MatchmakingQueueKeys
{
    /// <summary>4인 협동 던전의 일반 난이도 첫 번째 규칙 버전입니다.</summary>
    public const string CoopDungeonNormalV1 = "coop-dungeon-normal-v1";
}
