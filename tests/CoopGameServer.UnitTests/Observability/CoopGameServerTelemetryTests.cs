using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CoopGameServer.Observability;

namespace CoopGameServer.UnitTests.Observability;

/// <summary>관측 데이터가 상관관계에 필요한 정보만 기록하고 비밀·고카디널리티 값을 지표에서 제외하는지 검증합니다.</summary>
public sealed class CoopGameServerTelemetryTests
{
    [Fact]
    public void RewardMetricsUseOnlyBoundedOperationAndResultTags()
    {
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == CoopGameServerTelemetry.RewardMeterName)
            {
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) => measurements.Enqueue(Capture(instrument, tags)));
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) => measurements.Enqueue(Capture(instrument, tags)));
        listener.Start();

        CoopGameServerTelemetry.RecordRewardPersistence("administrator", "applied", 12.5);

        Assert.Equal(2, measurements.Count);
        Assert.All(measurements, measurement =>
        {
            Assert.Equal(2, measurement.Tags.Count);
            Assert.Equal("administrator", measurement.Tags["operation"]);
            Assert.Equal("applied", measurement.Tags["result"]);
            Assert.DoesNotContain("player_id", measurement.Tags.Keys);
            Assert.DoesNotContain("request_id", measurement.Tags.Keys);
        });
    }

    [Fact]
    public void MarkErrorRecordsExceptionTypeWithoutMessage()
    {
        const string secretMarker = "must-not-enter-telemetry";
        CapturedActivity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CoopGameServerTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured = new CapturedActivity(
                activity.Status,
                activity.StatusDescription,
                activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString())),
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = CoopGameServerTelemetry.StartActivity("test.failure"))
        {
            Assert.NotNull(activity);
            CoopGameServerTelemetry.MarkError(activity, new InvalidOperationException(secretMarker));
        }

        Assert.NotNull(captured);
        Assert.Equal(ActivityStatusCode.Error, captured.Status);
        Assert.Equal(nameof(InvalidOperationException), captured.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, captured.Tags["error.type"]);
        Assert.DoesNotContain(secretMarker, captured.StatusDescription, StringComparison.Ordinal);
        Assert.DoesNotContain(captured.Tags.Values, value =>
            value?.Contains(secretMarker, StringComparison.Ordinal) == true);
    }

    private static CapturedMeasurement Capture(
        Instrument instrument,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        return new CapturedMeasurement(
            instrument.Name,
            tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString()));
    }

    private sealed record CapturedMeasurement(
        string Name,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed record CapturedActivity(
        ActivityStatusCode Status,
        string? StatusDescription,
        IReadOnlyDictionary<string, string?> Tags);
}
