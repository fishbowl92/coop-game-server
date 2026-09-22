using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CoopGameServer.GrainContracts.Players;
using CoopGameServer.Grains.Players.Caching;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace CoopGameServer.IntegrationTests.Grains.Players;

/// <summary>실제 Redis 지연·중단에서 캐시 기반시설이 원본 경계로 실패를 넘기지 않는지 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class RedisPlayerProgressionCacheTests
{
    [Fact]
    public async Task TimeoutAndDisconnectedMutationsAreReportedWithoutEscaping()
    {
        var activities = new ConcurrentQueue<CapturedActivity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CoopGameServerTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(new CapturedActivity(
                activity.OperationName,
                activity.Status,
                activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString()))),
        };
        ActivitySource.AddActivityListener(activityListener);

        await using var redisContainer = new RedisBuilder("redis:7-alpine").Build();
        await redisContainer.StartAsync();

        var redisConfiguration = ConfigurationOptions.Parse(redisContainer.GetConnectionString());
        redisConfiguration.AllowAdmin = true;
        redisConfiguration.AbortOnConnectFail = false;
        redisConfiguration.BacklogPolicy = BacklogPolicy.FailFast;
        redisConfiguration.ConnectRetry = 1;
        redisConfiguration.ConnectTimeout = 1000;
        redisConfiguration.AsyncTimeout = 1000;

        await using var connection = await ConnectionMultiplexer.ConnectAsync(redisConfiguration);
        var options = new PlayerProgressionCacheOptions
        {
            KeyPrefix = "coopgame:test:isolated-progression:v1",
            EntryTtl = TimeSpan.FromMinutes(1),
            MaxJitter = TimeSpan.Zero,
            OperationTimeout = TimeSpan.FromMilliseconds(75),
        };
        var cache = new RedisPlayerProgressionCache(
            connection,
            options,
            NullLogger<RedisPlayerProgressionCache>.Instance);
        var playerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cachedResult = new PlayerProgressionPageResult(
            PlayerProgressionQueryError.None,
            Gold: 100,
            Items: [],
            NextContinuationToken: null,
            PlayerId: playerId,
            Nickname: "TimeoutPlayer",
            CreatedAt: now,
            UpdatedAt: now);

        await cache.WriteFirstPageAsync(playerId, pageSize: 20, cachedResult);
        var warmRead = await cache.ReadFirstPageAsync(playerId, pageSize: 20);
        Assert.Equal(PlayerProgressionCacheReadStatus.Hit, warmRead.Status);

        // Redis가 연결된 상태에서 실제 명령 처리를 750ms 멈춥니다.
        // 캐시의 75ms 한도가 먼저 끝나 Error를 반환해야 PostgreSQL Fallback으로 넘어갈 수 있습니다.
        await connection.GetDatabase().ExecuteAsync("CLIENT", "PAUSE", 750, "ALL");
        var timeoutRead = await cache.ReadFirstPageAsync(playerId, pageSize: 20);

        Assert.Equal(PlayerProgressionCacheReadStatus.Error, timeoutRead.Status);
        Assert.Null(timeoutRead.Value);

        // Pause 뒤 남은 명령이 끝나도록 기다린 다음 컨테이너를 중지해 연결 실패를 만듭니다.
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        var redisErrorOperations = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "CoopGameServer.PlayerProgressionCache" &&
                instrument.Name == "coopgame.player_progression_cache.redis_errors")
            {
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "operation" && tag.Value is string operation)
                {
                    redisErrorOperations.Enqueue(operation);
                }
            }
        });
        listener.Start();

        await redisContainer.StopAsync();

        // SET과 DEL이 실패해도 메서드는 예외를 외부로 던지지 않습니다.
        // 호출자는 PostgreSQL에서 얻은 조회 결과나 이미 Commit된 변경 성공을 유지할 수 있습니다.
        await cache.WriteFirstPageAsync(playerId, pageSize: 20, cachedResult);
        await cache.InvalidateAsync(playerId);

        Assert.Contains("write", redisErrorOperations);
        Assert.Contains("invalidate", redisErrorOperations);

        Assert.Contains(activities, activity =>
            activity.OperationName == "redis.progression.read" &&
            activity.Tags["coopgame.cache.result"] == "hit");
        Assert.Contains(activities, activity =>
            activity.OperationName == "redis.progression.read" &&
            activity.Status == ActivityStatusCode.Error &&
            activity.Tags["coopgame.cache.result"] == "error");
        Assert.Contains(activities, activity =>
            activity.OperationName == "redis.progression.write" &&
            activity.Status == ActivityStatusCode.Error);
        Assert.Contains(activities, activity =>
            activity.OperationName == "redis.progression.invalidate" &&
            activity.Status == ActivityStatusCode.Error);

        var traceText = string.Join(
            '|',
            activities.SelectMany(activity => activity.Tags)
                .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(playerId.ToString(), traceText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(options.KeyPrefix, traceText, StringComparison.Ordinal);
    }

    private sealed record CapturedActivity(
        string OperationName,
        ActivityStatusCode Status,
        IReadOnlyDictionary<string, string?> Tags);
}
