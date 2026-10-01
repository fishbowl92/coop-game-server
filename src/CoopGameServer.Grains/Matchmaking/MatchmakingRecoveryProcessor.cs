using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoopGameServer.Grains.Matchmaking;

/// <summary>미완료 매칭 의도를 DB에서 찾아 호출자 재전송 없이 이어가는 복구 처리기입니다.</summary>
public sealed partial class MatchmakingRecoveryProcessor(IDbContextFactory<GameDbContext> dbContextFactory,
    IGrainFactory grainFactory, TimeProvider timeProvider, ILogger<MatchmakingRecoveryProcessor> logger)
{
    public async Task RecoverPendingOperationsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        string[] keys;
        await using (var context = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var now = timeProvider.GetUtcNow();
            keys = await context.MatchmakingOperations.AsNoTracking()
                .Where(operation => operation.ResultPayloadJson == null && operation.NextAttemptAt <= now)
                .OrderBy(operation => operation.NextAttemptAt).ThenBy(operation => operation.OperationKey)
                .Select(operation => operation.OperationKey).Take(batchSize).ToArrayAsync(cancellationToken);
        }
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 취소는 복구 서비스의 대기만 중단합니다. 수락된 Grain 작업의 영속 처리는 계속됩니다.
                await grainFactory.GetGrain<IMatchmakingOperationGrain>(key).ResumeAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { LogRecoveryFailure(logger, key, exception); }
        }
    }

    [LoggerMessage(EventId = 4300, Level = LogLevel.Warning, Message = "매칭 작업 {OperationKey} 복구가 보류됐습니다")]
    private static partial void LogRecoveryFailure(ILogger logger, string operationKey, Exception exception);
}
