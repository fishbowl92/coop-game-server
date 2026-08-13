using CoopGameServer.Domain.Players;
using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Testcontainers.PostgreSql;

namespace CoopGameServer.IntegrationTests.Infrastructure;

/// <summary>
/// 테스트용 Orleans Silo·Client와 PostgreSQL 컨테이너의 생명 주기를 함께 관리합니다.
/// </summary>
/// <remarks>
/// PartyGrain 영속성 테스트는 Grain 실행 환경과 실제 PostgreSQL 제약조건이 모두 필요합니다.
/// compose.yaml의 개발 DB가 아니라 테스트가 끝나면 폐기되는 별도 컨테이너를 사용합니다.
/// </remarks>
public sealed class OrleansTestClusterFixture : IAsyncLifetime
{
    /// <summary>TestCluster가 Silo 설정에 전달할 GameDb 연결 문자열 키입니다.</summary>
    public const string GameDbConnectionStringKey = "ConnectionStrings:GameDb";

    private readonly PostgreSqlContainer _postgreSqlContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("coopgame_orleans_integration")
        .WithUsername("coopgame_orleans_test")
        // 일회성 테스트 컨테이너 전용 비밀번호이며 테스트가 끝나면 컨테이너와 함께 폐기됩니다.
        .WithPassword("orleans-integration-test-password")
        .Build();

    /// <summary>파티 테스트가 Grain 참조를 얻을 때 사용하는 테스트 클러스터입니다.</summary>
    public TestCluster Cluster { get; private set; } = null!;

    /// <summary>
    /// PostgreSQL 시작 → 마이그레이션 적용 → 테스트 Silo 배포 순서로 환경을 준비합니다.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _postgreSqlContainer.StartAsync();

        await using (var gameDbContext = CreateDbContext())
        {
            await gameDbContext.Database.MigrateAsync();
        }

        var clusterBuilder = new TestClusterBuilder();
        clusterBuilder.Properties[GameDbConnectionStringKey] = _postgreSqlContainer.GetConnectionString();
        clusterBuilder.AddSiloBuilderConfigurator<OrleansTestSiloConfigurator>();

        Cluster = clusterBuilder.Build();
        await Cluster.DeployAsync();
    }

    /// <summary>
    /// 테스트 Silo를 먼저 종료하고 그다음 PostgreSQL 컨테이너를 폐기합니다.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (Cluster is not null)
        {
            await Cluster.StopAllSilosAsync();
        }

        await _postgreSqlContainer.DisposeAsync();
    }

    /// <summary>
    /// 테스트 PostgreSQL 연결 문자열을 사용하는 독립적인 GameDbContext를 만듭니다.
    /// </summary>
    public GameDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseNpgsql(_postgreSqlContainer.GetConnectionString())
            .Options;

        return new GameDbContext(options);
    }

    /// <summary>
    /// 파티 명령에 사용할 실제 플레이어 행을 미리 등록합니다.
    /// </summary>
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
            // P + Guid 앞 19자는 20자 제한 안에서 테스트마다 사실상 고유한 닉네임을 만듭니다.
            var nickname = $"P{playerId:N}"[..Player.MaxNicknameLength];
            gameDbContext.Players.Add(new Player(playerId, nickname, now));
        }

        await gameDbContext.SaveChangesAsync();
    }
}

/// <summary>
/// TestCluster가 생성하는 각 Silo에 PartyGrain용 DbContext Factory를 등록합니다.
/// </summary>
public sealed class OrleansTestSiloConfigurator : IHostConfigurator
{
    /// <inheritdoc />
    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.ConfigureServices((hostContext, services) =>
        {
            var connectionString = hostContext.Configuration[
                OrleansTestClusterFixture.GameDbConnectionStringKey]
                ?? throw new InvalidOperationException("테스트 GameDb 연결 문자열이 없습니다.");

            services.AddPooledDbContextFactory<GameDbContext>(options =>
                options.UseNpgsql(connectionString));
        });
    }
}
