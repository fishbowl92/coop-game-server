using CoopGameServer.GrainContracts.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoopGameServer.Api.Health;

/// <summary>상태를 바꾸지 않는 Ping Grain으로 API에서 Silo까지의 호출 경로를 확인합니다.</summary>
public sealed class OrleansReadinessHealthCheck(IGrainFactory grainFactory) : IHealthCheck
{
    private const string ReadinessGrainId = "api-readiness";

    /// <summary>고정 식별자의 Ping Grain을 호출해 임의 Grain 생성을 제한합니다.</summary>
    /// <param name="context">상태 확인 등록 정보입니다.</param>
    /// <param name="cancellationToken">요청 제한 시간 또는 서버 종료 신호입니다.</param>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var grain = grainFactory.GetGrain<IPingGrain>(ReadinessGrainId);
            await grain.PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Orleans Silo 호출에 실패했습니다.", exception);
        }
    }
}
