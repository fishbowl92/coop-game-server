namespace CoopGameServer.GrainContracts.Players;

/// <summary>
/// 한 플레이어의 보상 변경과 진행도 조회를 순서대로 처리하는 Orleans Grain 계약입니다.
/// </summary>
/// <remarks>
/// Grain의 Guid 기본 키가 playerId입니다. 따라서 각 명령에 playerId를 다시 넣지 않아
/// 서로 다른 두 식별자가 들어오는 모순을 원천적으로 피합니다.
/// </remarks>
public interface IPlayerGrain : IGrainWithGuidKey
{
    /// <summary>관리자 전용 API가 검증한 명시적 보상을 지급합니다.</summary>
    /// <param name="command">멱등성 식별자와 지급할 재화·아이템 정보입니다.</param>
    Task<PlayerRewardCommandResult> GrantAdminRewardAsync(GrantPlayerRewardCommand command);

    /// <summary>GameRoomGrain이 확정한 게임 결과에 맞는 서버 보상을 처리합니다.</summary>
    /// <param name="command">방 식별자, 게임 결과, 서버 보상 정책 버전입니다.</param>
    Task<PlayerRewardCommandResult> CompleteGameAsync(CompletePlayerGameCommand command);

    /// <summary>PostgreSQL에 저장된 골드와 인벤토리를 한 페이지씩 조회합니다.</summary>
    /// <param name="query">한 번에 읽을 개수와 다음 조회 위치를 나타내는 연속 토큰입니다.</param>
    Task<PlayerProgressionPageResult> GetProgressionPageAsync(GetPlayerProgressionPageQuery query);
}
