using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace CoopGameServer.Observability;

/// <summary>공유 PostgreSQL 연결 풀로 새 연결을 열 수 있는지 확인합니다.</summary>
public sealed class PostgreSqlReadinessHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    /// <summary>데이터를 변경하지 않고 연결 가능 여부만 준비 상태로 반환합니다.</summary>
    /// <param name="context">상태 확인 등록 정보입니다.</param>
    /// <param name="cancellationToken">요청 제한 시간 또는 서버 종료 신호입니다.</param>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL 연결을 열 수 없습니다.", exception);
        }
    }
}
