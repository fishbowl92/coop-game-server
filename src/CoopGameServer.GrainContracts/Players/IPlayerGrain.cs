namespace CoopGameServer.GrainContracts.Players;

/// <summary>한 플레이어의 보상 변경과 진행도 조회를 순서대로 처리하는 Orleans Grain 계약입니다.</summary>
public interface IPlayerGrain : IGrainWithGuidKey
{
    /// <summary>관리자 전용 API가 검증한 명시적 보상을 지급합니다.</summary>
    Task<PlayerRewardCommandResult> GrantAdminRewardAsync(GrantPlayerRewardCommand command);

    /// <summary>GameRoomGrain이 확정한 게임 결과에 맞는 서버 보상을 처리합니다.</summary>
    Task<PlayerRewardCommandResult> CompleteGameAsync(CompletePlayerGameCommand command);

    /// <summary>PostgreSQL 원본 또는 Redis 캐시에서 프로필·골드·인벤토리를 조회합니다.</summary>
    Task<PlayerProgressionPageResult> GetProgressionPageAsync(GetPlayerProgressionPageQuery query);

    /// <summary>PostgreSQL 변경이 확정된 뒤 이 플레이어의 진행도 캐시를 삭제합니다.</summary>
    Task InvalidateProgressionCacheAsync();
}
