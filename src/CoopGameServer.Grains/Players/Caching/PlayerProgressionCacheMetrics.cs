using System.Diagnostics.Metrics;

namespace CoopGameServer.Grains.Players.Caching;

/// <summary>플레이어 ID 없이 낮은 Cardinality(카디널리티, 태그 값 종류 수) 지표를 기록합니다.</summary>
internal static class PlayerProgressionCacheMetrics
{
    internal const string MeterName = "CoopGameServer.PlayerProgressionCache";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "coopgame.player_progression_cache.requests");
    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>(
        "coopgame.player_progression_cache.fallbacks");
    private static readonly Counter<long> RedisErrors = Meter.CreateCounter<long>(
        "coopgame.player_progression_cache.redis_errors");
    private static readonly Histogram<double> RedisDuration = Meter.CreateHistogram<double>(
        "coopgame.player_progression_cache.redis_duration", unit: "ms");
    private static readonly Histogram<double> DatabaseFillDuration = Meter.CreateHistogram<double>(
        "coopgame.player_progression_cache.database_fill_duration", unit: "ms");

    internal static void RecordRequest(string result) =>
        Requests.Add(1, new KeyValuePair<string, object?>("result", result));

    internal static void RecordFallback(string reason) =>
        Fallbacks.Add(1, new KeyValuePair<string, object?>("reason", reason));

    internal static void RecordRedisError(string operation) =>
        RedisErrors.Add(1, new KeyValuePair<string, object?>("operation", operation));

    internal static void RecordRedisDuration(string operation, double milliseconds) =>
        RedisDuration.Record(milliseconds, new KeyValuePair<string, object?>("operation", operation));

    internal static void RecordDatabaseFillDuration(double milliseconds) =>
        DatabaseFillDuration.Record(milliseconds);
}
