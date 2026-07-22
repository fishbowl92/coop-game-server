using CoopGameServer.Contracts;

namespace CoopGameServer.UnitTests;

/// <summary>
/// 프로젝트 사이의 참조 관계가 올바른지 확인하는 테스트 모음입니다.
/// </summary>
public sealed class ProjectReferenceTests
{
    [Fact]
    public void ContractsProjectCanBeReferencedFromUnitTests()
    {
        // Contracts 프로젝트의 형식을 실제로 생성해 참조 설정이 유효한지 확인합니다.
        var marker = new ContractsAssemblyMarker();

        // marker가 null이 아니어야 생성과 참조가 정상적으로 이루어진 것입니다.
        Assert.NotNull(marker);
    }
}
