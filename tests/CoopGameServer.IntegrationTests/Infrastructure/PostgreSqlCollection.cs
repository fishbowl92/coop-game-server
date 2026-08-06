namespace CoopGameServer.IntegrationTests.Infrastructure;

/// <summary>
/// 같은 PostgreSQL Fixture를 공유하는 테스트 모음의 이름입니다.
/// </summary>
public static class PostgreSqlIntegrationTestGroup
{
    /// <summary>
    /// xUnit Collection 이름입니다.
    /// </summary>
    public const string Name = "PostgreSQL integration tests";
}

/// <summary>
/// 통합 테스트가 하나의 PostgreSQL 컨테이너를 공유하도록 선언합니다.
/// </summary>
/// <remarks>
/// 같은 컬렉션의 테스트는 병렬 실행되지 않으므로 ResetDataAsync가 다른 테스트와 충돌하지 않습니다.
/// 각 테스트 내부에서 만드는 100개 동시 보상 요청은 별도 코드로 의도적으로 병렬 실행합니다.
/// </remarks>
[CollectionDefinition(PostgreSqlIntegrationTestGroup.Name, DisableParallelization = true)]
public sealed class PostgreSqlCollectionDefinition : ICollectionFixture<PostgreSqlDatabaseFixture>
{
}
