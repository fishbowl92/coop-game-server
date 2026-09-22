using Npgsql;

namespace CoopGameServer.Persistence;

/// <summary>관측 데이터에 비밀 연결 문자열 대신 고정된 풀 이름을 사용하는 PostgreSQL 원본을 만듭니다.</summary>
public static class GameDbDataSourceFactory
{
    public const string DataSourceName = "GameDb";

    /// <summary>API와 Silo가 공유할 이름 있는 Npgsql 연결 풀을 만듭니다.</summary>
    /// <param name="connectionString">User Secrets 또는 운영 비밀 저장소에서 읽은 연결 문자열입니다.</param>
    public static NpgsqlDataSource Create(string connectionString)
    {
        return CreateBuilder(connectionString).Build();
    }

    /// <summary>테스트가 비밀 비노출용 풀 이름을 직접 검증할 수 있도록 Builder 구성을 분리합니다.</summary>
    internal static NpgsqlDataSourceBuilder CreateBuilder(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new NpgsqlDataSourceBuilder(connectionString)
        {
            // Npgsql 지표의 pool.name이 비밀번호가 포함될 수 있는 연결 문자열이 되지 않게 합니다.
            Name = DataSourceName,
        };
        return builder;
    }
}
