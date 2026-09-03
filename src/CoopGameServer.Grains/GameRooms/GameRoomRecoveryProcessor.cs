using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoopGameServer.Grains.GameRooms;

/// <summary>
/// PostgreSQL에서 아직 전달이 끝나지 않은 게임 방을 찾아 해당 GameRoomGrain을 깨웁니다.
/// </summary>
/// <remarks>
/// 이 처리기는 보상을 직접 지급하지 않습니다. 실제 결과 검증과 PlayerGrain 호출은
/// GameRoomGrain의 FinalizeCompletedRoomAsync가 담당합니다. 따라서 완료 직후 경로와
/// Silo 재시작 뒤 복구 경로가 동일한 멱등성 규칙을 사용합니다.
/// </remarks>
public sealed partial class GameRoomRecoveryProcessor(
    IDbContextFactory<GameDbContext> dbContextFactory,
    IGrainFactory grainFactory,
    TimeProvider timeProvider,
    ILogger<GameRoomRecoveryProcessor> logger)
{
    /// <summary>
    /// Pending 또는 재시도 시각이 지난 PendingRetry 결과가 있는 방을 최대 batchSize개 처리합니다.
    /// </summary>
    /// <param name="batchSize">한 주기에 조회할 서로 다른 게임 방의 최대 개수입니다.</param>
    /// <param name="cancellationToken">Silo 종료 시 DB 조회를 중단하기 위한 토큰입니다.</param>
    /// <returns>이번 주기에 발견·성공·실패한 방 수입니다.</returns>
    public async Task<GameRoomRecoveryBatchResult> RecoverDueRoomsAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var now = timeProvider.GetUtcNow();
        var dueRoomIds = await FindDueRoomIdsAsync(batchSize, now, cancellationToken);

        var succeededRoomCount = 0;
        var failedRoomCount = 0;

        foreach (var roomId in dueRoomIds)
        {
            try
            {
                // 과거 상태를 Worker가 직접 해석하지 않고 방을 소유한 Grain에게 복구를 맡깁니다.
                var gameRoom = grainFactory.GetGrain<IGameRoomGrain>(roomId);
                await gameRoom.FinalizeCompletedRoomAsync();
                succeededRoomCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 정상 종료 요청은 실패로 기록하지 않고 상위 BackgroundService까지 전달합니다.
                throw;
            }
            catch (Exception exception)
            {
                // 한 방의 장애가 같은 Batch에 포함된 다른 방의 복구까지 막지 않게 격리합니다.
                // 결과 행은 미완료 상태로 남으므로 다음 주기에 다시 조회할 수 있습니다.
                failedRoomCount++;
                LogRoomRecoveryFailure(logger, roomId, exception);
            }
        }

        return new GameRoomRecoveryBatchResult(
            dueRoomIds.Length,
            succeededRoomCount,
            failedRoomCount);
    }

    /// <summary>이번 주기에 처리할 방 ID만 조회하고 DB Context를 Grain 호출 전에 반환합니다.</summary>
    private async Task<Guid[]> FindDueRoomIdsAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // 한 방에는 플레이어별로 여러 game_results 행이 있으므로 RoomId만 고른 뒤 Distinct로 중복을 제거합니다.
        // Pending은 최초 전달 전 상태이고, PendingRetry는 NextAttemptAt이 지난 경우에만 다시 깨웁니다.
        return await gameDbContext.GameResults
            .AsNoTracking()
            .Where(result => result.DeliveryStatus == GameResultDeliveryStatus.Pending
                || (result.DeliveryStatus == GameResultDeliveryStatus.PendingRetry
                    && (result.NextAttemptAt == null || result.NextAttemptAt <= now)))
            .Select(result => result.RoomId)
            .Distinct()
            .OrderBy(roomId => roomId)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>한 방의 복구 실패를 구조화된 RoomId와 함께 기록합니다.</summary>
    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Warning,
        Message = "게임 방 {RoomId}의 결과 전달 자동 복구에 실패했습니다")]
    private static partial void LogRoomRecoveryFailure(
        ILogger logger,
        Guid roomId,
        Exception exception);
}
