namespace CoopGameServer.IntegrationTests.Infrastructure;

/// <summary>
/// PartyGrain 테스트가 하나의 TestCluster Fixture를 공유하도록 묶는 xUnit Collection입니다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OrleansTestClusterSuite : ICollectionFixture<OrleansTestClusterFixture>
{
    /// <summary>테스트 클래스의 Collection 특성에서 사용하는 고정 이름입니다.</summary>
    public const string Name = nameof(OrleansTestClusterSuite);
}
