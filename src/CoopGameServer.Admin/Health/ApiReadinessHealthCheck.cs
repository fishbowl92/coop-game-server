using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoopGameServer.Admin.Health;

/// <summary>관리자 UI가 사용하는 API의 생존 상태를 확인합니다.</summary>
public sealed class ApiReadinessHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    /// <summary>API의 무상태 생존 엔드포인트를 호출합니다.</summary>
    /// <param name="context">상태 확인 등록 정보입니다.</param>
    /// <param name="cancellationToken">요청 제한 시간 또는 서버 종료 신호입니다.</param>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient("GameApi");
            using var response = await client.GetAsync("health/live", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"API 생존 확인이 HTTP {(int)response.StatusCode}를 반환했습니다.");
        }
        catch (HttpRequestException exception)
        {
            return HealthCheckResult.Unhealthy("API 생존 엔드포인트에 연결할 수 없습니다.", exception);
        }
    }
}
