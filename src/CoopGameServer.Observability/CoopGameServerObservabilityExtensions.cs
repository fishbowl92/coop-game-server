using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orleans.Diagnostics;

namespace CoopGameServer.Observability;

/// <summary>API와 Silo에 같은 OpenTelemetry 수집·내보내기 정책을 적용합니다.</summary>
public static class CoopGameServerObservabilityExtensions
{
    private const string OtlpEndpointKey = "OpenTelemetry:OtlpEndpoint";
    private const string StandardOtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string StandardServiceNameKey = "OTEL_SERVICE_NAME";

    /// <summary>추적과 지표 수집을 등록하고 설정된 경우에만 OTLP로 내보냅니다.</summary>
    public static OpenTelemetryBuilder AddCoopGameServerObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultServiceName,
        bool includeAspNetCore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        var serviceName = ResolveServiceName(configuration, defaultServiceName);
        var serviceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        var otlpEndpoint = ResolveOtlpEndpoint(configuration);

        return services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(
                        CoopGameServerTelemetry.ActivitySourceName,
                        ActivitySources.AllActivitySourceName)
                    .AddNpgsql();

                if (includeAspNetCore)
                {
                    tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        // 예외 메시지·Stack trace에 입력값이나 연결 정보가 섞일 수 있어 자동 기록하지 않습니다.
                        // HTTP 상태와 직접 만든 하위 Activity의 제한된 error.type으로 실패 위치를 판별합니다.
                        options.RecordException = false;
                    });
                }

                if (otlpEndpoint is not null)
                {
                    tracing.AddOtlpExporter(options => ConfigureExporter(options, otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(
                        CoopGameServerTelemetry.RewardMeterName,
                        CoopGameServerTelemetry.PlayerProgressionCacheMeterName,
                        "Microsoft.Orleans",
                        "Npgsql")
                    .AddRuntimeInstrumentation();

                if (includeAspNetCore)
                {
                    metrics.AddMeter(
                        "Microsoft.AspNetCore.Hosting",
                        "Microsoft.AspNetCore.Server.Kestrel",
                        "Microsoft.AspNetCore.Routing",
                        "Microsoft.AspNetCore.RateLimiting");
                }

                if (otlpEndpoint is not null)
                {
                    metrics.AddOtlpExporter(options => ConfigureExporter(options, otlpEndpoint));
                }
            });
    }

    /// <summary>구조화 로그를 Trace ID와 함께 OTLP로 내보내도록 등록합니다.</summary>
    public static ILoggingBuilder AddCoopGameServerOpenTelemetryLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string defaultServiceName)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        var serviceName = ResolveServiceName(configuration, defaultServiceName);
        var serviceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        var otlpEndpoint = ResolveOtlpEndpoint(configuration);

        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.SetResourceBuilder(
                ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: serviceVersion));

            if (otlpEndpoint is not null)
            {
                options.AddOtlpExporter(exporter => ConfigureExporter(exporter, otlpEndpoint));
            }
        });

        return logging;
    }

    private static string ResolveServiceName(IConfiguration configuration, string defaultServiceName)
    {
        var configured = configuration[StandardServiceNameKey]?.Trim();
        return string.IsNullOrEmpty(configured) ? defaultServiceName : configured;
    }

    private static Uri? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        var configured = configuration[StandardOtlpEndpointKey]
            ?? configuration[OtlpEndpointKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"{StandardOtlpEndpointKey} 또는 {OtlpEndpointKey}는 절대 HTTP(S) 주소여야 합니다.");
        }

        return endpoint;
    }

    private static void ConfigureExporter(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
    }
}
