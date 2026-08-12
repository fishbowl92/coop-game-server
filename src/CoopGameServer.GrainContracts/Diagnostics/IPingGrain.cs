using Orleans;

namespace CoopGameServer.GrainContracts.Diagnostics;

/// <summary>
/// API가 Orleans 연결 경로를 확인할 때 호출하는 최소 Grain 계약입니다.
/// </summary>
/// <remarks>
/// 이 인터페이스에는 "어떻게" 처리하는지 쓰지 않습니다.
/// API와 Silo가 합의해야 하는 호출 이름, 매개변수, 반환 형식만 둡니다.
/// 파티 기능을 만들기 전 API → Silo → Grain 흐름을 독립적으로 검증하기 위한
/// 진단용 계약이며, 게임 상태나 PostgreSQL 데이터를 변경하지 않습니다.
/// </remarks>
public interface IPingGrain : IGrainWithStringKey
{
    /// <summary>
    /// 현재 Grain이 호출 가능하다는 응답을 반환합니다.
    /// </summary>
    /// <returns>
    /// Grain 식별자와 응답을 만든 UTC 시각이 담긴 진단 결과입니다.
    /// </returns>
    Task<PingGrainResponse> PingAsync();
}
