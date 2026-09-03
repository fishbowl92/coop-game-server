using CoopGameServer.Grains.GameRooms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoopGameServer.Silo.Recovery;

/// <summary>
/// Silo가 실행되는 동안 미완료 게임 결과를 일정 주기로 찾아 복구하는 백그라운드 서비스입니다.
/// </summary>
/// <remarks>
/// BackgroundService는 별도 콘솔 프로그램이 아니라 Silo 호스트 안에서 함께 시작·종료됩니다.
/// 실제 DB 조회와 Grain 호출은 테스트 가능한 GameRoomRecoveryProcessor에 위임합니다.
/// </remarks>
public sealed partial class GameRoomRecoveryService(
    GameRoomRecoveryProcessor recoveryProcessor,
    IOptions<GameRoomRecoveryOptions> options,
    ILogger<GameRoomRecoveryService> logger) : BackgroundService
{
    private readonly GameRoomRecoveryOptions _options = options.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(
            logger,
            _options.PollingInterval,
            _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 시작 직후에도 한 번 실행하므로 Silo 중단 중 쌓인 Pending을 즉시 확인합니다.
                var result = await recoveryProcessor.RecoverDueRoomsAsync(
                    _options.BatchSize,
                    stoppingToken);

                if (result.DiscoveredRoomCount > 0)
                {
                    LogRecoveryCycleCompleted(
                        logger,
                        result.DiscoveredRoomCount,
                        result.SucceededRoomCount,
                        result.FailedRoomCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 일시적인 DB 조회 장애가 BackgroundService 자체를 영구 종료시키지 않게 합니다.
                LogRecoveryCycleFailure(logger, exception);
            }

            try
            {
                await Task.Delay(_options.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogServiceStopped(logger);
    }

    /// <summary>복구 서비스가 사용할 설정값과 함께 시작 로그를 남깁니다.</summary>
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Information,
        Message = "게임 결과 자동 복구 서비스를 시작합니다 조회 간격 {PollingInterval} 최대 방 수 {BatchSize}")]
    private static partial void LogServiceStarted(
        ILogger logger,
        TimeSpan pollingInterval,
        int batchSize);

    /// <summary>실제 대상이 있었던 복구 주기의 처리 결과를 남깁니다.</summary>
    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "게임 결과 자동 복구 주기를 완료했습니다 발견 {DiscoveredRoomCount} 성공 {SucceededRoomCount} 실패 {FailedRoomCount}")]
    private static partial void LogRecoveryCycleCompleted(
        ILogger logger,
        int discoveredRoomCount,
        int succeededRoomCount,
        int failedRoomCount);

    /// <summary>DB 조회처럼 주기 전체가 실패한 경우 원인 예외를 기록합니다.</summary>
    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Error,
        Message = "게임 결과 자동 복구 주기 실행에 실패했습니다")]
    private static partial void LogRecoveryCycleFailure(ILogger logger, Exception exception);

    /// <summary>Silo 종료 요청에 따라 복구 서비스가 정상 종료됐음을 기록합니다.</summary>
    [LoggerMessage(
        EventId = 4203,
        Level = LogLevel.Information,
        Message = "게임 결과 자동 복구 서비스를 종료합니다")]
    private static partial void LogServiceStopped(ILogger logger);
}
