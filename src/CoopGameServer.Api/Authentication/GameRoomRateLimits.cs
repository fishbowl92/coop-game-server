using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CoopGameServer.Api.Authentication;

/// <summary>단일 API 인스턴스의 빠른 남용 방어입니다. Grain의 연결 자격 검증을 대체하지 않습니다.</summary>
public static class GameRoomRateLimits
{
    public static IServiceCollection AddGameRoomRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            Add(options, "room-heartbeat", 2, TimeSpan.FromSeconds(1));
            Add(options, "room-reconnect", 5, TimeSpan.FromMinutes(1));
            Add(options, "room-command", 30, TimeSpan.FromSeconds(1));
            Add(options, "room-read", 30, TimeSpan.FromSeconds(1));
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
        });
        return services;
    }

    private static void Add(RateLimiterOptions options, string name, int count, TimeSpan window)
    {
        options.AddPolicy(name, context =>
        {
            context.User.TryGetPlayerId(out var player);
            // 방 ID·GUID 표기·연결 ID를 바꿔도 같은 인증 주체는 같은 예산을 사용합니다.
            // 임의의 방마다 제한기 인스턴스를 만들지 않아 존재하지 않는 방으로 분할을 늘릴 수도 없습니다.
            return RateLimitPartition.GetFixedWindowLimiter(player, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = count, Window = window, QueueLimit = 0, AutoReplenishment = true });
        });
    }
}
