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
            var room = context.Request.RouteValues["roomId"]?.ToString();
            // 연결 ID만 바꿔 제한을 회피하지 못하도록 인증된 본인과 방으로 구분합니다.
            return RateLimitPartition.GetFixedWindowLimiter($"{player}:{room}", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = count, Window = window, QueueLimit = 0, AutoReplenishment = true });
        });
    }
}
