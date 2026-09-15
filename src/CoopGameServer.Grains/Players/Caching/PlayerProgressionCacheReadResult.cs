using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Grains.Players.Caching;

public enum PlayerProgressionCacheReadStatus
{
    Hit = 0,
    Miss = 1,
    Error = 2,
    Corrupt = 3,
}

/// <summary>캐시 조회 상태와 검증된 진행도 값을 함께 반환합니다.</summary>
public readonly record struct PlayerProgressionCacheReadResult(
    PlayerProgressionCacheReadStatus Status,
    PlayerProgressionPageResult? Value);
