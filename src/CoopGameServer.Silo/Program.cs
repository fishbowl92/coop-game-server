using System.Net.Sockets;
using CoopGameServer.Grains.GameRooms;
using CoopGameServer.Grains.Matchmaking;
using CoopGameServer.Grains.Persistence;
using CoopGameServer.Grains.Players.Caching;
using CoopGameServer.Observability;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Rewards;
using CoopGameServer.Silo.Recovery;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

// Silo는 Grain 구현체를 실제로 실행하는 Orleans 서버 프로세스입니다.
// 이 프로젝트는 HTTP 요청을 직접 받지 않습니다. HTTP 요청은 Api 프로젝트가 받고,
// Api가 Grain 메서드를 호출하면 Silo가 해당 Grain을 활성화하여 실행합니다.
var host = Host.CreateDefaultBuilder(args)
    // 저장소 루트에서 --project로 실행해도 출력 폴더의 appsettings 파일을 동일하게 읽습니다.
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureLogging((hostContext, logging) =>
    {
        logging.AddCoopGameServerOpenTelemetryLogging(
            hostContext.Configuration,
            "CoopGameServer.Silo");
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddCoopGameServerObservability(
            hostContext.Configuration,
            "CoopGameServer.Silo",
            includeAspNetCore: true);

        // Api 프로젝트와 같은 User Secrets 저장소에서 GameDb 연결 문자열을 읽습니다.
        // Grain은 명령마다 짧게 DbContext를 빌려 쓰므로 Factory 형태로 등록합니다.
        var gameDbConnectionString = hostContext.Configuration.GetConnectionString("GameDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:GameDb 설정이 없습니다. User Secrets에 PostgreSQL 연결 문자열을 설정하세요.");

        var gameDbDataSource = GameDbDataSourceFactory.Create(gameDbConnectionString);
        services.AddSingleton(gameDbDataSource);
        services.AddPooledDbContextFactory<GameDbContext>(options =>
            options.UseNpgsql(gameDbDataSource));

        // PlayerGrain이 사용할 보상 Writer는 호출마다 Factory에서 새 DbContext를 빌립니다.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRewardWriter, PostgreSqlRewardWriter>();

        // Redis 연결 한 개를 Silo 전체가 공유합니다. AbortOnConnectFail=false는 시작 시 Redis가
        // 잠시 꺼져 있어도 Silo를 살리고, 각 캐시 호출의 짧은 제한 시간 뒤 DB로 전환하게 합니다.
        var redisConnectionString = hostContext.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis 설정이 없습니다.");
        var cacheOptions = new PlayerProgressionCacheOptions();
        hostContext.Configuration
            .GetSection(PlayerProgressionCacheOptions.SectionName)
            .Bind(cacheOptions);
        cacheOptions.Validate();

        var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString);
        redisConfiguration.AbortOnConnectFail = false;
        // 연결 중 명령을 쌓지 않아 오래된 캐시 쓰기가 복구 뒤 늦게 실행되는 일을 막습니다.
        redisConfiguration.BacklogPolicy = BacklogPolicy.FailFast;
        // 실제 Cache 작업은 아래 값보다 짧은 OperationTimeout을 WaitAsync에서 별도로 적용합니다.
        redisConfiguration.ConnectTimeout = Math.Max(
            1000,
            (int)cacheOptions.OperationTimeout.TotalMilliseconds);
        redisConfiguration.AsyncTimeout = Math.Max(
            1000,
            (int)cacheOptions.OperationTimeout.TotalMilliseconds);

        services.AddSingleton(cacheOptions);
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redisConfiguration));
        services.AddSingleton<IPlayerProgressionCache, RedisPlayerProgressionCache>();

        // Silo 재시작 뒤 남아 있는 Pending·PendingRetry 게임 결과를 자동으로 다시 전달합니다.
        // Options(옵션)는 기본 5초·100개를 사용하며 이후 설정 파일로 값을 바꿀 수 있습니다.
        services.AddOptions<GameRoomRecoveryOptions>()
            .Bind(hostContext.Configuration.GetSection(GameRoomRecoveryOptions.SectionName))
            .Validate(options => options.PollingInterval > TimeSpan.Zero, "조회 간격은 0초보다 커야 합니다")
            .Validate(options => options.BatchSize > 0, "Batch 크기는 0보다 커야 합니다")
            .ValidateOnStart();
        services.AddSingleton<GameRoomRecoveryProcessor>();
        services.AddSingleton<MatchmakingRecoveryProcessor>();
    })
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.ConfigureServices(services =>
        {
            services.AddHealthChecks()
                .AddCheck<PostgreSqlReadinessHealthCheck>(
                    "postgresql",
                    tags: ["ready"],
                    timeout: TimeSpan.FromSeconds(3));
        });
        webBuilder.Configure(app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
                {
                    // 프로세스와 HTTP 파이프라인만 확인하고 외부 의존성을 호출하지 않습니다.
                    Predicate = _ => false,
                });
                endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
                {
                    Predicate = registration => registration.Tags.Contains("ready"),
                });
            });
        });
    })
    .UseOrleans((hostContext, siloBuilder) =>
    {
        var advertisedHost = hostContext.Configuration["Orleans:AdvertisedHost"];
        if (string.IsNullOrWhiteSpace(advertisedHost))
        {
            // 설정이 없으면 기존 개발 PC의 localhost 단일 Silo 구성을 유지합니다.
            siloBuilder.UseLocalhostClustering();
        }
        else
        {
            var siloPort = hostContext.Configuration.GetValue("Orleans:SiloPort", 11111);
            var gatewayPort = hostContext.Configuration.GetValue("Orleans:GatewayPort", 30000);
            var clusterId = hostContext.Configuration["Orleans:ClusterId"] ?? "coopgame-local";
            var serviceId = hostContext.Configuration["Orleans:ServiceId"] ?? "CoopGameServer";

            // 한 컨테이너 Silo이므로 개발용 멤버십을 유지하되, 다른 컨테이너가 접근할 주소를 광고합니다.
            siloBuilder.UseLocalhostClustering(
                siloPort,
                gatewayPort,
                primarySiloEndpoint: null,
                serviceId,
                clusterId);
            siloBuilder.ConfigureEndpoints(
                advertisedHost,
                siloPort,
                gatewayPort,
                AddressFamily.InterNetwork,
                listenOnAnyHostAddress: true);
        }

        // API에서 시작된 Trace Context를 Grain 실행과 하위 PostgreSQL·Redis Activity로 전달합니다.
        siloBuilder.AddActivityPropagation();
        siloBuilder.AddIncomingGrainCallFilter<PersistenceExceptionFilter>();
    })
    .ConfigureServices(services =>
    {
        // Orleans Silo HostedService 뒤에 등록하여 Silo가 준비된 다음 복구를 시작하고,
        // 종료할 때는 역순으로 복구 Worker를 먼저 멈춘 뒤 Orleans를 종료합니다.
        services.AddHostedService<GameRoomRecoveryService>();
    })
    .Build();

await host.RunAsync();
