namespace CoopGameServer.Grains.Players.Caching;

/// <summary>플레이어 진행도 Redis 캐시의 키, 만료 시간과 실패 대기 한도를 정의합니다.</summary>
public sealed class PlayerProgressionCacheOptions
{
    public const string SectionName = "PlayerProgressionCache";

    /// <summary>다른 Redis 키와 충돌하지 않게 붙이는 버전 포함 접두사입니다.</summary>
    public string KeyPrefix { get; set; } = "coopgame:player-progression:v1";

    /// <summary>TTL(Time To Live, 자동 만료 시간)의 기본값입니다.</summary>
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>동시 만료를 줄이려고 기본 TTL에 더하는 무작위 최대 시간입니다.</summary>
    public TimeSpan MaxJitter { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Redis 장애 때 PostgreSQL로 전환하기까지 기다리는 한도입니다.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new InvalidOperationException("Player progression cache key prefix must not be empty.");
        }

        if (EntryTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Player progression cache TTL must be greater than zero.");
        }

        if (MaxJitter < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Player progression cache jitter must not be negative.");
        }

        if (OperationTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Player progression cache timeout must be greater than zero.");
        }
    }
}
