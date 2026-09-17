using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Players;
using CoopGameServer.Grains.Players.Caching;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Rewards;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Grains.Players;

/// <summary>
/// 한 플레이어의 보상 변경 명령을 순서대로 처리하고 PostgreSQL 진행도를 조회하는 Orleans Grain입니다.
/// </summary>
/// <remarks>
/// Grain Key는 Player ID입니다. 같은 Player ID의 호출은 Orleans 실행 큐에서 순차 처리되지만,
/// 골드와 인벤토리의 최종 원본은 Grain 메모리가 아니라 PostgreSQL에만 둡니다.
/// </remarks>
public sealed class PlayerGrain : Grain, IPlayerGrain
{
    private const int MaxRewardReasonLength = 100;
    private const int MaxQueueKeyLength = 100;
    private const int MaxProgressionPageSize = 100;
    private const int MaxContinuationTokenLength = 64;
    private const string ContinuationTokenVersion = "v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IRewardWriter _rewardWriter;
    private readonly IDbContextFactory<GameDbContext> _gameDbContextFactory;
    private readonly IPlayerProgressionCache _progressionCache;

    /// <summary>보상 영속성, DB 조회와 Redis 캐시 경계를 주입받습니다.</summary>
    public PlayerGrain(
        IRewardWriter rewardWriter,
        IDbContextFactory<GameDbContext> gameDbContextFactory,
        IPlayerProgressionCache progressionCache)
    {
        ArgumentNullException.ThrowIfNull(rewardWriter);
        ArgumentNullException.ThrowIfNull(gameDbContextFactory);
        ArgumentNullException.ThrowIfNull(progressionCache);

        _rewardWriter = rewardWriter;
        _gameDbContextFactory = gameDbContextFactory;
        _progressionCache = progressionCache;
    }

    /// <inheritdoc />
    public async Task<PlayerRewardCommandResult> GrantAdminRewardAsync(
        GrantPlayerRewardCommand command)
    {
        var playerId = this.GetPrimaryKey();

        if (!TryCreateRewardWriteCommand(playerId, command, out var writeCommand))
        {
            return Rejected(PlayerRewardCommandError.InvalidRequest);
        }

        // 외부 HTTP 취소 토큰을 Grain과 Writer로 전달하지 않습니다.
        // 호출이 시작된 보상은 DB 성공·업무 거부·기반시설 예외 중 하나로 끝까지 확정합니다.
        var writeResult = await _rewardWriter.WriteAsync(writeCommand);

        var result = writeResult.Error switch
        {
            RewardWriteError.None => Applied(writeCommand, writeResult),
            RewardWriteError.PlayerNotFound => Rejected(PlayerRewardCommandError.PlayerNotFound),
            RewardWriteError.IdempotencyConflict => Rejected(PlayerRewardCommandError.IdempotencyConflict),
            RewardWriteError.InvalidAdministrator => Rejected(PlayerRewardCommandError.InvalidAdministrator),
            _ => throw new InvalidOperationException(
                $"지원하지 않는 보상 쓰기 오류입니다: {writeResult.Error}"),
        };

        if (writeResult.Error == RewardWriteError.None)
        {
            // 재생 응답도 삭제합니다. 이전 시도가 DB Commit 뒤 캐시 삭제 전에 끊겼을 수 있기 때문입니다.
            await _progressionCache.InvalidateAsync(playerId);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PlayerRewardCommandResult> CompleteGameAsync(
        CompletePlayerGameCommand command)
    {
        var playerId = this.GetPrimaryKey();

        if (!IsValidCompleteGameCommand(playerId, command))
        {
            return Rejected(PlayerRewardCommandError.InvalidRequest);
        }

        if (!GameCompletionRewardPolicy.TryEvaluate(command, out var reward))
        {
            return Rejected(PlayerRewardCommandError.UnsupportedRewardPolicy);
        }

        if (reward is null)
        {
            // 패배·취소에는 지급할 값이 없으므로 RewardWriter와 reward_audits를 사용하지 않습니다.
            // 다만 삭제되거나 잘못 지정된 Player까지 정상 무보상으로 처리하지 않도록 존재 여부는 확인합니다.
            await using var gameDbContext = await _gameDbContextFactory.CreateDbContextAsync();
            var playerExists = await gameDbContext.Players
                .AsNoTracking()
                .AnyAsync(player => player.Id == playerId);

            return playerExists
                ? NoReward()
                : Rejected(PlayerRewardCommandError.PlayerNotFound);
        }

        var writeCommand = new RewardWriteCommand(
            command.RequestId,
            playerId,
            reward.GoldAmount,
            reward.ItemId,
            reward.ItemQuantity,
            reward.Reason);
        var writeResult = await _rewardWriter.WriteAsync(writeCommand);

        var result = writeResult.Error switch
        {
            RewardWriteError.None => Applied(writeCommand, writeResult),
            RewardWriteError.PlayerNotFound => Rejected(PlayerRewardCommandError.PlayerNotFound),
            RewardWriteError.IdempotencyConflict => Rejected(PlayerRewardCommandError.IdempotencyConflict),
            RewardWriteError.InvalidAdministrator => Rejected(PlayerRewardCommandError.InvalidAdministrator),
            _ => throw new InvalidOperationException(
                $"지원하지 않는 보상 쓰기 오류입니다: {writeResult.Error}"),
        };

        if (writeResult.Error == RewardWriteError.None)
        {
            // 재생 응답도 삭제합니다. 이전 시도가 DB Commit 뒤 캐시 삭제 전에 끊겼을 수 있기 때문입니다.
            await _progressionCache.InvalidateAsync(playerId);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PlayerProgressionPageResult> GetProgressionPageAsync(
        GetPlayerProgressionPageQuery query)
    {
        if (query is null || query.PageSize is < 1 or > MaxProgressionPageSize)
        {
            return ProgressionFailure(PlayerProgressionQueryError.InvalidPageSize);
        }

        if (!TryDecodeContinuationToken(query.ContinuationToken, out var lastItemId))
        {
            return ProgressionFailure(PlayerProgressionQueryError.InvalidContinuationToken);
        }

        var playerId = this.GetPrimaryKey();
        if (playerId == Guid.Empty)
        {
            return ProgressionFailure(PlayerProgressionQueryError.PlayerNotFound);
        }

        var isFirstPage = query.ContinuationToken is null;
        if (isFirstPage)
        {
            var cacheRead = await _progressionCache.ReadFirstPageAsync(playerId, query.PageSize);
            if (cacheRead is
                {
                    Status: PlayerProgressionCacheReadStatus.Hit,
                    Value: not null,
                })
            {
                return cacheRead.Value;
            }

            var fallbackReason = cacheRead.Status switch
            {
                PlayerProgressionCacheReadStatus.Miss => "miss",
                PlayerProgressionCacheReadStatus.Error => "error",
                PlayerProgressionCacheReadStatus.Corrupt => "corrupt",
                _ => "unknown",
            };
            PlayerProgressionCacheMetrics.RecordFallback(fallbackReason);
        }

        var databaseStartedAt = Stopwatch.GetTimestamp();
        await using var gameDbContext = await _gameDbContextFactory.CreateDbContextAsync();

        // 아래 세 SELECT가 서로 다른 시점의 값을 섞지 않도록 PostgreSQL의 반복 읽기 스냅샷으로 묶습니다.
        // 관리자 API가 아직 PlayerGrain을 우회하는 전환 기간에도 골드와 인벤토리는 한 시점 기준으로 반환됩니다.
        await using var readTransaction = await gameDbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead);

        var player = await gameDbContext.Players
            .AsNoTracking()
            .Where(entity => entity.Id == playerId)
            .Select(entity => new
            {
                entity.Id,
                entity.Nickname,
                entity.CreatedAt,
                entity.UpdatedAt,
            })
            .SingleOrDefaultAsync();

        if (player is null)
        {
            await readTransaction.CommitAsync();
            return ProgressionFailure(PlayerProgressionQueryError.PlayerNotFound);
        }

        // 아직 지갑 행이 없는 정상 Player는 골드 0으로 표현합니다.
        var gold = await gameDbContext.PlayerWallets
            .AsNoTracking()
            .Where(wallet => wallet.PlayerId == playerId)
            .Select(wallet => (long?)wallet.Gold)
            .SingleOrDefaultAsync() ?? 0;

        // PageSize보다 하나 더 읽어 다음 페이지 존재 여부를 별도 COUNT 질의 없이 판단합니다.
        var fetchedItems = await gameDbContext.InventoryItems
            .AsNoTracking()
            .Where(item => item.PlayerId == playerId && item.ItemId > lastItemId)
            .OrderBy(item => item.ItemId)
            .Take(query.PageSize + 1)
            .Select(item => new PlayerInventoryItemSnapshot(
                item.ItemId,
                item.Quantity,
                item.UpdatedAt))
            .ToArrayAsync();

        await readTransaction.CommitAsync();

        var hasNextPage = fetchedItems.Length > query.PageSize;
        var pageItems = hasNextPage
            ? fetchedItems[..query.PageSize]
            : fetchedItems;
        var nextContinuationToken = hasNextPage
            ? EncodeContinuationToken(pageItems[^1].ItemId)
            : null;

        var result = new PlayerProgressionPageResult(
            PlayerProgressionQueryError.None,
            gold,
            pageItems,
            nextContinuationToken,
            player.Id,
            player.Nickname,
            player.CreatedAt,
            player.UpdatedAt);

        if (isFirstPage)
        {
            PlayerProgressionCacheMetrics.RecordDatabaseFillDuration(
                Stopwatch.GetElapsedTime(databaseStartedAt).TotalMilliseconds);
            await _progressionCache.WriteFirstPageAsync(playerId, query.PageSize, result);
        }

        return result;
    }

    /// <inheritdoc />
    public Task InvalidateProgressionCacheAsync()
    {
        var playerId = this.GetPrimaryKey();
        return playerId == Guid.Empty
            ? Task.CompletedTask
            : _progressionCache.InvalidateAsync(playerId);
    }

    /// <summary>관리자 Grain 계약을 검증하고 정규화된 Persistence 명령으로 변환합니다.</summary>
    private static bool TryCreateRewardWriteCommand(
        Guid playerId,
        GrantPlayerRewardCommand? command,
        out RewardWriteCommand writeCommand)
    {
        writeCommand = null!;

        if (playerId == Guid.Empty ||
            command is null ||
            command.RequestId == Guid.Empty ||
            command.AdministratorAccountId == Guid.Empty ||
            command.GoldAmount < 0)
        {
            return false;
        }

        var hasItemId = command.ItemId is not null;
        var hasItemQuantity = command.ItemQuantity is not null;

        if (hasItemId != hasItemQuantity ||
            command.ItemId is <= 0 ||
            command.ItemQuantity is <= 0 ||
            (command.GoldAmount == 0 && !hasItemId))
        {
            return false;
        }

        var normalizedReason = command.Reason?.Trim();
        if (string.IsNullOrEmpty(normalizedReason) ||
            normalizedReason.Length > MaxRewardReasonLength)
        {
            return false;
        }

        writeCommand = new RewardWriteCommand(
            command.RequestId,
            playerId,
            command.GoldAmount,
            command.ItemId,
            command.ItemQuantity,
            normalizedReason,
            command.AdministratorAccountId);
        return true;
    }

    /// <summary>게임 완료 계약의 식별자·문자열·열거형 형식을 검사합니다.</summary>
    private static bool IsValidCompleteGameCommand(
        Guid playerId,
        CompletePlayerGameCommand? command)
    {
        return playerId != Guid.Empty &&
               command is not null &&
               command.RequestId != Guid.Empty &&
               command.RoomId != Guid.Empty &&
               !string.IsNullOrWhiteSpace(command.QueueKey) &&
               command.QueueKey.Length <= MaxQueueKeyLength &&
               Enum.IsDefined(command.Outcome) &&
               command.Outcome is not GameOutcome.None &&
               command.RewardPolicyVersion > 0;
    }

    /// <summary>Persistence 성공 결과를 Orleans 직렬화용 결과로 복사합니다.</summary>
    private static PlayerRewardCommandResult Applied(
        RewardWriteCommand command,
        RewardWriteResult writeResult)
    {
        var receipt = writeResult.Receipt
            ?? throw new InvalidOperationException("성공한 보상 쓰기 결과에 영수증이 없습니다.");

        if (receipt.PlayerId != command.PlayerId ||
            receipt.RequestId != command.RequestId ||
            receipt.GoldAmount != command.GoldAmount ||
            receipt.ItemId != command.ItemId ||
            receipt.ItemQuantity != command.ItemQuantity ||
            !string.Equals(receipt.Reason, command.Reason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "보상 Writer 영수증의 식별자 또는 보상 내용이 Grain 명령과 일치하지 않습니다.");
        }

        var playerReceipt = new PlayerRewardReceipt(
            receipt.RewardAuditId,
            receipt.RequestId,
            receipt.PlayerId,
            receipt.GoldAmount,
            receipt.ItemId,
            receipt.ItemQuantity,
            receipt.Reason,
            receipt.CreatedAt);

        return new PlayerRewardCommandResult(
            writeResult.IsReplay,
            PlayerRewardCommandStatus.Applied,
            PlayerRewardCommandError.None,
            playerReceipt);
    }

    /// <summary>DB를 변경하지 않은 예상 업무 거부 결과를 일관된 형태로 만듭니다.</summary>
    private static PlayerRewardCommandResult Rejected(PlayerRewardCommandError error)
    {
        if (error is PlayerRewardCommandError.None || !Enum.IsDefined(error))
        {
            throw new ArgumentOutOfRangeException(
                nameof(error),
                error,
                "거부 결과에는 None이 아닌 정의된 Player 보상 오류가 필요합니다.");
        }

        return new PlayerRewardCommandResult(
            IsReplay: false,
            PlayerRewardCommandStatus.Rejected,
            error,
            Receipt: null);
    }

    /// <summary>정책상 지급할 보상이 없지만 명령 처리는 정상 완료됐음을 나타냅니다.</summary>
    private static PlayerRewardCommandResult NoReward()
    {
        return new PlayerRewardCommandResult(
            IsReplay: false,
            PlayerRewardCommandStatus.NoReward,
            PlayerRewardCommandError.None,
            Receipt: null);
    }

    /// <summary>진행도 조회 오류에서도 Items를 null이 아닌 빈 배열로 고정합니다.</summary>
    private static PlayerProgressionPageResult ProgressionFailure(
        PlayerProgressionQueryError error)
    {
        return new PlayerProgressionPageResult(
            error,
            Gold: 0,
            Items: [],
            NextContinuationToken: null,
            PlayerId: Guid.Empty,
            Nickname: null,
            CreatedAt: default,
            UpdatedAt: default);
    }

    /// <summary>마지막 Item ID를 버전이 포함된 불투명 Base64Url 토큰으로 변환합니다.</summary>
    private static string EncodeContinuationToken(int lastItemId)
    {
        var tokenPayload = $"{ContinuationTokenVersion}:{lastItemId.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToBase64String(StrictUtf8.GetBytes(tokenPayload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>연속 토큰의 Base64Url·버전·양수 Item ID 형식을 검증합니다.</summary>
    private static bool TryDecodeContinuationToken(string? token, out int lastItemId)
    {
        lastItemId = 0;

        if (token is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxContinuationTokenLength)
        {
            return false;
        }

        // 서버가 발급하는 Base64Url은 영문·숫자·'-'·'_'만 사용하며 '=' 패딩이나 공백을 포함하지 않습니다.
        // 같은 위치를 여러 문자열로 표현하지 못하게 하여 로그·캐시·테스트에서 토큰 표현을 하나로 고정합니다.
        if (token.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        var paddedToken = token.Replace('-', '+').Replace('_', '/');
        paddedToken = (paddedToken.Length % 4) switch
        {
            0 => paddedToken,
            2 => paddedToken + "==",
            3 => paddedToken + "=",
            _ => string.Empty,
        };

        if (paddedToken.Length == 0)
        {
            return false;
        }

        try
        {
            var tokenPayload = StrictUtf8.GetString(Convert.FromBase64String(paddedToken));
            var separatorIndex = tokenPayload.IndexOf(':', StringComparison.Ordinal);

            if (separatorIndex <= 0 ||
                tokenPayload[(separatorIndex + 1)..].Contains(':', StringComparison.Ordinal) ||
                !string.Equals(
                    tokenPayload[..separatorIndex],
                    ContinuationTokenVersion,
                    StringComparison.Ordinal) ||
                !int.TryParse(
                    tokenPayload[(separatorIndex + 1)..],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out lastItemId) ||
                lastItemId <= 0)
            {
                lastItemId = 0;
                return false;
            }

            // 디코딩은 같지만 불필요한 선행 0이나 다른 pad bit를 가진 비정규 표현도 거부합니다.
            if (!string.Equals(EncodeContinuationToken(lastItemId), token, StringComparison.Ordinal))
            {
                lastItemId = 0;
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
