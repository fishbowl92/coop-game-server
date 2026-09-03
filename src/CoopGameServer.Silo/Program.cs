using CoopGameServer.Grains.GameRooms;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Rewards;
using CoopGameServer.Silo.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Silo는 Grain 구현체를 실제로 실행하는 Orleans 서버 프로세스입니다.
// 이 프로젝트는 HTTP 요청을 직접 받지 않습니다. HTTP 요청은 Api 프로젝트가 받고,
// Api가 Grain 메서드를 호출하면 Silo가 해당 Grain을 활성화하여 실행합니다.
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        // Api 프로젝트와 같은 User Secrets 저장소에서 GameDb 연결 문자열을 읽습니다.
        // PartyGrain은 명령마다 짧게 DbContext를 빌려 쓰므로 Factory 형태로 등록합니다.
        var gameDbConnectionString = hostContext.Configuration.GetConnectionString("GameDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:GameDb 설정이 없습니다. User Secrets에 PostgreSQL 연결 문자열을 설정하세요.");

        services.AddPooledDbContextFactory<GameDbContext>(options =>
            options.UseNpgsql(gameDbConnectionString));

        // PlayerGrain이 사용할 보상 Writer는 호출마다 Factory에서 새 DbContext를 빌립니다.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRewardWriter, PostgreSqlRewardWriter>();

        // Silo 재시작 뒤 남아 있는 Pending·PendingRetry 게임 결과를 자동으로 다시 전달합니다.
        // Options(옵션)는 기본 5초·100개를 사용하며 이후 설정 파일로 값을 바꿀 수 있습니다.
        services.AddOptions<GameRoomRecoveryOptions>()
            .Bind(hostContext.Configuration.GetSection(GameRoomRecoveryOptions.SectionName))
            .Validate(options => options.PollingInterval > TimeSpan.Zero, "조회 간격은 0초보다 커야 합니다")
            .Validate(options => options.BatchSize > 0, "Batch 크기는 0보다 커야 합니다")
            .ValidateOnStart();
        services.AddSingleton<GameRoomRecoveryProcessor>();
    })
    .UseOrleans(siloBuilder =>
    {
        // 개발 PC에서만 사용하는 단일 Silo 구성입니다.
        // Orleans의 Silo 간 통신 포트(기본 11111)와 API Client 접속 게이트웨이 포트
        // (기본 30000)를 localhost에 준비합니다. 운영 환경의 클러스터 구성은 아직 범위 밖입니다.
        siloBuilder.UseLocalhostClustering();
    })
    .ConfigureServices(services =>
    {
        // Orleans Silo HostedService 뒤에 등록하여 Silo가 준비된 다음 복구를 시작하고,
        // 종료할 때는 역순으로 복구 Worker를 먼저 멈춘 뒤 Orleans를 종료합니다.
        services.AddHostedService<GameRoomRecoveryService>();
    })
    .Build();

await host.RunAsync();
