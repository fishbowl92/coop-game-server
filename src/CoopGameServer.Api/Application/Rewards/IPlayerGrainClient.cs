using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>API 계층이 Orleans의 큰 IGrainFactory 대신 사용하는 PlayerGrain 호출 경계입니다.</summary>
public interface IPlayerGrainClient
{
    Task<PlayerRewardCommandResult> GrantAdminRewardAsync(
        Guid playerId,
        GrantPlayerRewardCommand command);

    /// <summary>인증된 진행도 HTTP 요청을 PlayerGrain에 전달합니다.</summary>
    Task<PlayerProgressionPageResult> GetProgressionPageAsync(
        Guid playerId,
        GetPlayerProgressionPageQuery query);

    /// <summary>API가 직접 변경한 프로필의 Redis 진행도 캐시를 삭제합니다.</summary>
    Task InvalidateProgressionCacheAsync(Guid playerId);
}
