using Orleans.TestingHost;

namespace CoopGameServer.IntegrationTests.Infrastructure;

/// <summary>
/// 테스트 프로세스 안에서 Orleans Silo와 Client를 한 번 시작해 여러 파티 테스트가 공유하게 합니다.
/// </summary>
/// <remarks>
/// TestCluster는 실제 배포 Silo가 아니라 Grain 호출·직렬화·기본 단일 스레드 실행 규칙을
/// 자동 테스트에서 확인하기 위한 인메모리 클러스터입니다.
/// </remarks>
public sealed class OrleansTestClusterFixture : IDisposable
{
    /// <summary>
    /// 파티 테스트가 Grain 참조를 얻는 데 사용하는 클러스터입니다.
    /// </summary>
    public TestCluster Cluster { get; } = new TestClusterBuilder().Build();

    /// <summary>
    /// xUnit이 Collection Fixture를 만들 때 테스트용 Silo와 Client를 시작합니다.
    /// </summary>
    public OrleansTestClusterFixture()
    {
        Cluster.Deploy();
    }

    /// <summary>
    /// Collection의 모든 테스트가 끝나면 테스트용 Silo를 종료합니다.
    /// </summary>
    public void Dispose()
    {
        Cluster.StopAllSilos();
    }
}
