using CoopGameServer.Domain.Players;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Rewards;
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
/// Grain 영속성·재시작 테스트는 Orleans 실행 환경과 실제 PostgreSQL 제약조건이 모두 필요합니다.
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

    /// <summary>통합 테스트가 Grain 참조를 얻을 때 사용하는 테스트 클러스터입니다.</summary>
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

        // 기본 2개 Silo를 유지해 Grain이 노드 사이에 배치되는 환경도 계속 검증합니다.
        // 영속성 복원 테스트는 RestartAllSilosAsync로 모든 활성화를 제거한 뒤 다시 호출합니다.
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
    /// 현재 테스트 클러스터의 모든 Silo를 차례로 재시작해 기존 Grain 활성화를 제거합니다.
    /// </summary>
    /// <remarks>
    /// Primary 하나만 재시작하면 대상 Grain이 Secondary에 남아 DB 복원 없이 테스트가 통과할 수 있습니다.
    /// 호출자는 재시작 도중 Grain 요청을 보내지 않아야 하며, 완료 뒤 새 Proxy 호출로 복원을 검증합니다.
    /// </remarks>
    public async Task RestartAllSilosAsync()
    {
        // RestartSiloAsync가 Cluster.Silos를 갱신하므로 반복 전에 기존 Handle을 배열로 복사합니다.
        var siloHandles = Cluster.Silos.ToArray();

        foreach (var siloHandle in siloHandles)
        {
            await Cluster.RestartSiloAsync(siloHandle);
        }
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
/// TestCluster가 생성하는 각 Silo에 Grain용 DB Factory와 보상 Writer를 등록합니다.
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

            // PlayerGrain은 HTTP 요청과 무관한 서버 시각과 호출별 DbContext를 사용하는 Writer에 의존합니다.
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IRewardWriter, PostgreSqlRewardWriter>();
        });
    }
}
