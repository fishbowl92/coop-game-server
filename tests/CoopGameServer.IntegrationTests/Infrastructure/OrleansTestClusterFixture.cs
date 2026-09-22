using CoopGameServer.Domain.Players;
using CoopGameServer.Grains.Players.Caching;
using CoopGameServer.Observability;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Rewards;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace CoopGameServer.IntegrationTests.Infrastructure;

/// <summary>테스트용 Orleans Silo·Client, PostgreSQL과 Redis 컨테이너의 생명 주기를 관리합니다.</summary>
public sealed class OrleansTestClusterFixture : IAsyncLifetime
{
    public const string GameDbConnectionStringKey = "ConnectionStrings:GameDb";
    public const string RedisConnectionStringKey = "ConnectionStrings:Redis";

    private readonly PostgreSqlContainer _postgreSqlContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("coopgame_orleans_integration")
        .WithUsername("coopgame_orleans_test")
        .WithPassword("orleans-integration-test-password")
        .Build();

    private readonly RedisContainer _redisContainer = new RedisBuilder("redis:7-alpine")
        .Build();

    public TestCluster Cluster { get; private set; } = null!;

    /// <summary>별도 장애 TestCluster가 같은 PostgreSQL 원본을 사용할 때 필요한 주소입니다.</summary>
    public string GameDbConnectionString => _postgreSqlContainer.GetConnectionString();

    /// <summary>테스트 코드가 Redis 내용을 직접 검증할 때 사용하는 일회성 컨테이너 주소입니다.</summary>
    public string RedisConnectionString => _redisContainer.GetConnectionString();

    /// <summary>PostgreSQL·Redis 시작, Migration(마이그레이션) 적용, 테스트 Silo 배포 순서로 준비합니다.</summary>
    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _postgreSqlContainer.StartAsync(),
            _redisContainer.StartAsync());

        await using (var gameDbContext = CreateDbContext())
        {
            await gameDbContext.Database.MigrateAsync();
        }

        var clusterBuilder = new TestClusterBuilder();
        clusterBuilder.Properties[GameDbConnectionStringKey] = _postgreSqlContainer.GetConnectionString();
        clusterBuilder.Properties[RedisConnectionStringKey] = _redisContainer.GetConnectionString();
        clusterBuilder.AddSiloBuilderConfigurator<OrleansTestSiloConfigurator>();
        clusterBuilder.AddSiloBuilderConfigurator<OrleansTestActivityPropagationConfigurator>();
        clusterBuilder.AddClientBuilderConfigurator<OrleansTestClientActivityPropagationConfigurator>();

        Cluster = clusterBuilder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        if (Cluster is not null)
        {
            await Cluster.StopAllSilosAsync();
        }

        await _redisContainer.DisposeAsync();
        await _postgreSqlContainer.DisposeAsync();
    }

    public GameDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseNpgsql(_postgreSqlContainer.GetConnectionString())
            .Options;

        return new GameDbContext(options);
    }

    public async Task RestartAllSilosAsync()
    {
        var siloHandles = Cluster.Silos.ToArray();

        foreach (var siloHandle in siloHandles)
        {
            await Cluster.RestartSiloAsync(siloHandle);
        }
    }

    public async Task RegisterPlayersAsync(params Guid[] playerIds)
    {
        var distinctPlayerIds = playerIds.Distinct().ToArray();
        await using var gameDbContext = CreateDbContext();

        var existingPlayerIds = await gameDbContext.Players
            .Where(player => distinctPlayerIds.Contains(player.Id))
            .Select(player => player.Id)
            .ToArrayAsync();
        var existingPlayerIdSet = existingPlayerIds.ToHashSet();
        var now = DateTimeOffset.UtcNow;

        foreach (var playerId in distinctPlayerIds.Where(playerId => !existingPlayerIdSet.Contains(playerId)))
        {
            var nickname = $"P{playerId:N}"[..Player.MaxNicknameLength];
            gameDbContext.Players.Add(new Player(playerId, nickname, now));
        }

        await gameDbContext.SaveChangesAsync();
    }
}

/// <summary>테스트 Silo가 호출자의 W3C Trace Context를 Grain 실행으로 복원하게 합니다.</summary>
public sealed class OrleansTestActivityPropagationConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddActivityPropagation();
    }
}

/// <summary>테스트 Client가 현재 Activity의 Trace Context를 Orleans 메시지에 싣게 합니다.</summary>
public sealed class OrleansTestClientActivityPropagationConfigurator : IClientBuilderConfigurator
{
    public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
    {
        clientBuilder.AddActivityPropagation();
    }
}

/// <summary>TestCluster의 각 Silo에 PostgreSQL과 Redis 의존성을 등록합니다.</summary>
public sealed class OrleansTestSiloConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.ConfigureServices((hostContext, services) =>
        {
            var gameDbConnectionString = hostContext.Configuration[
                OrleansTestClusterFixture.GameDbConnectionStringKey]
                ?? throw new InvalidOperationException("테스트 GameDb 연결 문자열이 없습니다.");
            var redisConnectionString = hostContext.Configuration[
                OrleansTestClusterFixture.RedisConnectionStringKey]
                ?? throw new InvalidOperationException("테스트 Redis 연결 문자열이 없습니다.");

            // 운영 Silo와 같은 ActivitySource·Meter를 구독해 테스트에서도 실제 추적 생성 조건을 재현합니다.
            // 테스트 설정에는 OTLP Endpoint가 없으므로 외부 수집기로 내보내지는 않습니다.
            var observabilityConfiguration = new ConfigurationBuilder().Build();
            services.AddCoopGameServerObservability(
                observabilityConfiguration,
                "CoopGameServer.TestSilo",
                includeAspNetCore: false);

            var gameDbDataSource = GameDbDataSourceFactory.Create(gameDbConnectionString);
            services.AddSingleton(gameDbDataSource);
            services.AddPooledDbContextFactory<GameDbContext>(options =>
                options.UseNpgsql(gameDbDataSource));
            services.AddSingleton<TimeProvider>(CombatTestTimeProvider.Shared);
            services.AddSingleton<IRewardWriter, PostgreSqlRewardWriter>();

            // 짧은 TTL과 timeout은 만료·장애 테스트 시간을 줄입니다. FailFast는 연결이 끊겼을 때
            // 명령을 쌓아 두었다가 뒤늦게 실행하지 않고 즉시 PostgreSQL 대체 경로로 보냅니다.
            var cacheOptions = new PlayerProgressionCacheOptions
            {
                KeyPrefix = "coopgame:test:player-progression:v1",
                EntryTtl = TimeSpan.FromSeconds(5),
                MaxJitter = TimeSpan.Zero,
                OperationTimeout = TimeSpan.FromSeconds(2),
            };
            var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString);
            redisConfiguration.AbortOnConnectFail = false;
            redisConfiguration.BacklogPolicy = BacklogPolicy.FailFast;
            redisConfiguration.ConnectRetry = 1;
            redisConfiguration.ConnectTimeout = 1000;
            redisConfiguration.AsyncTimeout = 1000;

            services.AddSingleton(cacheOptions);
            services.AddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(redisConfiguration));
            services.AddSingleton<IPlayerProgressionCache, RedisPlayerProgressionCache>();
        });
    }
}
