using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CoopGameServer.IntegrationTests.Infrastructure;

/// <summary>
/// 통합 테스트 전용 PostgreSQL 컨테이너의 생명 주기(Lifecycle, 생성부터 폐기까지의 과정)를 관리합니다.
/// </summary>
/// <remarks>
/// 이 컨테이너는 compose.yaml의 로컬 개발 DB를 재사용하지 않습니다.
/// 테스트 실행마다 독립적인 빈 DB를 만들고 EF Core 마이그레이션을 적용하므로,
/// 개발 데이터가 테스트 결과에 영향을 주거나 테스트가 개발 데이터를 오염시키지 않습니다.
/// </remarks>
public sealed class PostgreSqlDatabaseFixture : IAsyncLifetime, IDbContextFactory<GameDbContext>
{
    private readonly PostgreSqlContainer _postgreSqlContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("coopgame_integration")
        .WithUsername("coopgame_test")
        // 테스트 전용 컨테이너의 비밀번호입니다. 운영·개발 비밀 정보와 무관하며 테스트 종료 시 컨테이너와 함께 폐기됩니다.
        .WithPassword("integration-test-password")
        .Build();

    /// <summary>
    /// xUnit이 이 Fixture를 처음 사용할 때 컨테이너를 시작하고 최신 스키마를 적용합니다.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _postgreSqlContainer.StartAsync();

        await using var gameDbContext = CreateDbContext();
        await gameDbContext.Database.MigrateAsync();
    }

    /// <summary>
    /// 모든 통합 테스트가 끝나면 테스트 컨테이너를 폐기합니다.
    /// </summary>
    public Task DisposeAsync()
    {
        return _postgreSqlContainer.DisposeAsync().AsTask();
    }

    /// <summary>
    /// 테스트마다 독립적인 Change Tracker를 갖는 GameDbContext를 생성합니다.
    /// </summary>
    /// <returns>테스트 전용 PostgreSQL 연결 문자열을 사용하는 DB 작업 객체입니다.</returns>
    public GameDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseNpgsql(_postgreSqlContainer.GetConnectionString())
            .Options;

        return new GameDbContext(options);
    }

    /// <summary>
    /// 각 테스트 시작 전 테스트 데이터만 비웁니다. 스키마와 마이그레이션 이력은 유지합니다.
    /// </summary>
    public async Task ResetDataAsync()
    {
        await using var gameDbContext = CreateDbContext();

        // players를 기준으로 CASCADE를 사용하면 외래 키로 연결된 지갑·인벤토리·보상 이력도 함께 비웁니다.
        // 이것은 Testcontainers가 만든 일회성 DB에만 실행되며 로컬 Compose DB에는 영향을 주지 않습니다.
        await gameDbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE players CASCADE;");
    }
}
