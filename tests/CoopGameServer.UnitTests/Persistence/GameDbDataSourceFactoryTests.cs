using CoopGameServer.Persistence;

namespace CoopGameServer.UnitTests.Persistence;

/// <summary>Npgsql 지표가 비밀 연결 문자열을 풀 식별자로 사용하지 않게 하는 구성을 검증합니다.</summary>
public sealed class GameDbDataSourceFactoryTests
{
    [Fact]
    public void CreateUsesStableNameInsteadOfConnectionString()
    {
        const string connectionString =
            "Host=127.0.0.1;Port=5432;Database=telemetry_test;Username=tester;Password=secret-marker";

        var builder = GameDbDataSourceFactory.CreateBuilder(connectionString);

        Assert.Equal(GameDbDataSourceFactory.DataSourceName, builder.Name);
        Assert.DoesNotContain("secret-marker", builder.Name, StringComparison.Ordinal);
    }
}
