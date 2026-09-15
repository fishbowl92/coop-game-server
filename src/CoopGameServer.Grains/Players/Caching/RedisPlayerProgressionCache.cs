using System.Diagnostics;
using System.Text.Json;
using CoopGameServer.GrainContracts.Players;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CoopGameServer.Grains.Players.Caching;

/// <summary>
/// Redis Hash(해시, 한 키 아래 여러 필드를 저장하는 자료구조)에 PageSize별 첫 페이지를 저장합니다.
/// Redis 실패는 PostgreSQL 원본 조회와 성공한 DB 쓰기를 방해하지 않습니다.
/// </summary>
public sealed class RedisPlayerProgressionCache : IPlayerProgressionCache
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Guid, Exception?> InvalidJsonLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(1, nameof(InvalidJsonLog)),
            "Invalid progression cache JSON was discarded for player {PlayerId}.");
    private static readonly Action<ILogger, Guid, Exception?> ReadFailureLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2, nameof(ReadFailureLog)),
            "Progression cache read failed for player {PlayerId}; PostgreSQL will be used.");
    private static readonly Action<ILogger, Guid, Exception?> WriteFailureLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(3, nameof(WriteFailureLog)),
            "Progression cache write failed for player {PlayerId}; the DB result remains valid.");
    private static readonly Action<ILogger, Guid, Exception?> InvalidationFailureLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(4, nameof(InvalidationFailureLog)),
            "Progression cache invalidation failed for player {PlayerId}; TTL limits staleness.");
    private static readonly Action<ILogger, Guid, Exception?> CorruptDeleteFailureLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Debug,
            new EventId(5, nameof(CorruptDeleteFailureLog)),
            "Corrupt progression cache key could not be deleted for player {PlayerId}.");

    private readonly IDatabase _database;
    private readonly PlayerProgressionCacheOptions _options;
    private readonly ILogger<RedisPlayerProgressionCache> _logger;

    public RedisPlayerProgressionCache(
        IConnectionMultiplexer connectionMultiplexer,
        PlayerProgressionCacheOptions options,
        ILogger<RedisPlayerProgressionCache> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _database = connectionMultiplexer.GetDatabase();
        _options = options;
        _logger = logger;
    }

    public async Task<PlayerProgressionCacheReadResult> ReadFirstPageAsync(Guid playerId, int pageSize)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var value = await _database.HashGetAsync(BuildKey(playerId), BuildField(pageSize))
                .WaitAsync(_options.OperationTimeout);

            if (value.IsNullOrEmpty)
            {
                PlayerProgressionCacheMetrics.RecordRequest("miss");
                return new(PlayerProgressionCacheReadStatus.Miss, null);
            }

            CachedPlayerProgressionPage? payload;

            try
            {
                payload = JsonSerializer.Deserialize<CachedPlayerProgressionPage>(
                    value.ToString(),
                    SerializerOptions);
            }
            catch (JsonException exception)
            {
                InvalidJsonLog(_logger, playerId, exception);
                await DeleteCorruptKeyAsync(playerId);
                PlayerProgressionCacheMetrics.RecordRequest("corrupt");
                return new(PlayerProgressionCacheReadStatus.Corrupt, null);
            }

            if (payload is null ||
                payload.SchemaVersion != SchemaVersion ||
                payload.PlayerId != playerId ||
                payload.PageSize != pageSize ||
                payload.Result.Error != PlayerProgressionQueryError.None ||
                payload.Result.PlayerId != playerId ||
                payload.Result.Nickname is null ||
                payload.Result.Items is null)
            {
                await DeleteCorruptKeyAsync(playerId);
                PlayerProgressionCacheMetrics.RecordRequest("corrupt");
                return new(PlayerProgressionCacheReadStatus.Corrupt, null);
            }

            PlayerProgressionCacheMetrics.RecordRequest("hit");
            return new(PlayerProgressionCacheReadStatus.Hit, payload.Result);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            ReadFailureLog(_logger, playerId, exception);
            PlayerProgressionCacheMetrics.RecordRequest("error");
            PlayerProgressionCacheMetrics.RecordRedisError("read");
            return new(PlayerProgressionCacheReadStatus.Error, null);
        }
        finally
        {
            PlayerProgressionCacheMetrics.RecordRedisDuration(
                "read",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    public async Task WriteFirstPageAsync(
        Guid playerId,
        int pageSize,
        PlayerProgressionPageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Error != PlayerProgressionQueryError.None ||
            result.PlayerId != playerId ||
            result.Nickname is null)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var serialized = JsonSerializer.Serialize(
                new CachedPlayerProgressionPage(SchemaVersion, playerId, pageSize, result),
                SerializerOptions);
            var key = BuildKey(playerId);
            var transaction = _database.CreateTransaction();
            var setTask = transaction.HashSetAsync(key, BuildField(pageSize), serialized);
            var expireTask = transaction.KeyExpireAsync(key, CreateEntryTtl());
            var committed = await transaction.ExecuteAsync().WaitAsync(_options.OperationTimeout);

            if (!committed)
            {
                throw new RedisException("Redis cache transaction was not committed.");
            }

            await Task.WhenAll(setTask, expireTask).WaitAsync(_options.OperationTimeout);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            WriteFailureLog(_logger, playerId, exception);
            PlayerProgressionCacheMetrics.RecordRedisError("write");
        }
        finally
        {
            PlayerProgressionCacheMetrics.RecordRedisDuration(
                "write",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    public async Task InvalidateAsync(Guid playerId)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            await _database.KeyDeleteAsync(BuildKey(playerId))
                .WaitAsync(_options.OperationTimeout);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            InvalidationFailureLog(_logger, playerId, exception);
            PlayerProgressionCacheMetrics.RecordRedisError("invalidate");
        }
        finally
        {
            PlayerProgressionCacheMetrics.RecordRedisDuration(
                "invalidate",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private async Task DeleteCorruptKeyAsync(Guid playerId)
    {
        try
        {
            await _database.KeyDeleteAsync(BuildKey(playerId))
                .WaitAsync(_options.OperationTimeout);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            CorruptDeleteFailureLog(_logger, playerId, exception);
            PlayerProgressionCacheMetrics.RecordRedisError("delete_corrupt");
        }
    }

    private RedisKey BuildKey(Guid playerId) => $"{_options.KeyPrefix}:{playerId:N}";

    private static RedisValue BuildField(int pageSize) => $"first:{pageSize}";

    private TimeSpan CreateEntryTtl()
    {
        var jitterMilliseconds = Random.Shared.NextDouble() * _options.MaxJitter.TotalMilliseconds;
        return _options.EntryTtl + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    private static bool IsRedisFailure(Exception exception) =>
        exception is RedisException or TimeoutException;

    private sealed record CachedPlayerProgressionPage(
        int SchemaVersion,
        Guid PlayerId,
        int PageSize,
        PlayerProgressionPageResult Result);
}
