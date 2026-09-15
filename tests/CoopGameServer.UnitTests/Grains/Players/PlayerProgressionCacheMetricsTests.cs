using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CoopGameServer.Grains.Players.Caching;

namespace CoopGameServer.UnitTests.Grains.Players;

/// <summary>운영 지표 이름과 낮은 Cardinality(카디널리티, 태그 값 종류 수) 규칙을 고정합니다.</summary>
public sealed class PlayerProgressionCacheMetricsTests
{
    [Fact]
    public void RecordsCacheOutcomeFallbackErrorAndDurationsWithoutPlayerId()
    {
        var measurements = new ConcurrentQueue<Measurement>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == PlayerProgressionCacheMetrics.MeterName)
            {
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) => measurements.Enqueue(CreateMeasurement(instrument, tags)));
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) => measurements.Enqueue(CreateMeasurement(instrument, tags)));
        listener.Start();

        PlayerProgressionCacheMetrics.RecordRequest("hit");
        PlayerProgressionCacheMetrics.RecordFallback("miss");
        PlayerProgressionCacheMetrics.RecordRedisError("read");
        PlayerProgressionCacheMetrics.RecordRedisDuration("read", 1.5);
        PlayerProgressionCacheMetrics.RecordDatabaseFillDuration(2.5);

        Assert.Contains(
            measurements,
            item => item.Name == "coopgame.player_progression_cache.requests" &&
                    item.TagKey == "result" &&
                    item.TagValue == "hit");
        Assert.Contains(
            measurements,
            item => item.Name == "coopgame.player_progression_cache.fallbacks" &&
                    item.TagKey == "reason" &&
                    item.TagValue == "miss");
        Assert.Contains(
            measurements,
            item => item.Name == "coopgame.player_progression_cache.redis_errors" &&
                    item.TagKey == "operation" &&
                    item.TagValue == "read");
        Assert.Contains(
            measurements,
            item => item.Name == "coopgame.player_progression_cache.redis_duration" &&
                    item.TagKey == "operation");
        Assert.Contains(
            measurements,
            item => item.Name == "coopgame.player_progression_cache.database_fill_duration" &&
                    item.TagKey is null);
        Assert.DoesNotContain(
            measurements,
            item => string.Equals(item.TagKey, "player_id", StringComparison.Ordinal));
    }

    private static Measurement CreateMeasurement(
        Instrument instrument,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        return tags.IsEmpty
            ? new Measurement(instrument.Name, null, null)
            : new Measurement(instrument.Name, tags[0].Key, tags[0].Value?.ToString());
    }

    private sealed record Measurement(string Name, string? TagKey, string? TagValue);
}
