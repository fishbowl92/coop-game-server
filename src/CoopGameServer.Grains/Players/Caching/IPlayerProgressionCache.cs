using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Grains.Players.Caching;

/// <summary>PlayerGrain이 Redis 구현 세부사항과 분리된 상태로 사용하는 캐시 경계입니다.</summary>
public interface IPlayerProgressionCache
{
    Task<PlayerProgressionCacheReadResult> ReadFirstPageAsync(Guid playerId, int pageSize);

    Task WriteFirstPageAsync(Guid playerId, int pageSize, PlayerProgressionPageResult result);

    Task InvalidateAsync(Guid playerId);
}
