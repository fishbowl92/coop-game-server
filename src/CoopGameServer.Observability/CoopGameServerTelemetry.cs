using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CoopGameServer.Observability;

/// <summary>서비스들이 공유하는 추적·지표 이름과 안전한 기록 함수를 제공합니다.</summary>
public static class CoopGameServerTelemetry
{
    public const string ActivitySourceName = "CoopGameServer.Application";
    public const string RewardMeterName = "CoopGameServer.Rewards";
    public const string PlayerProgressionCacheMeterName = "CoopGameServer.PlayerProgressionCache";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter RewardMeter = new(RewardMeterName);
    private static readonly Counter<long> RewardOperations = RewardMeter.CreateCounter<long>(
        "coopgame.reward.persistence.operations");
    private static readonly Histogram<double> RewardDuration = RewardMeter.CreateHistogram<double>(
        "coopgame.reward.persistence.duration",
        unit: "ms");

    /// <summary>현재 요청 Activity의 자식 작업을 시작합니다.</summary>
    /// <param name="name">대시보드에서 구간을 구분할 낮은 종류 수의 작업 이름입니다.</param>
    public static Activity? StartActivity(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ActivitySource.StartActivity(name, ActivityKind.Internal);
    }

    /// <summary>예외 메시지나 입력값을 복사하지 않고 예외 형식만 실패 Activity에 남깁니다.</summary>
    public static void MarkError(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity?.SetTag("error.type", exception.GetType().FullName);
    }

    /// <summary>보상 저장 횟수와 지연을 낮은 Cardinality 결과 태그로 기록합니다.</summary>
    public static void RecordRewardPersistence(string operation, string result, double milliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);

        var tags = new TagList
        {
            { "operation", operation },
            { "result", result },
        };
        RewardOperations.Add(1, tags);
        RewardDuration.Record(milliseconds, tags);
    }
}
