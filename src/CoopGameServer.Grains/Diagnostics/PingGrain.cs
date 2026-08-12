using CoopGameServer.GrainContracts.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans;

namespace CoopGameServer.Grains.Diagnostics;

/// <summary>
/// <see cref="IPingGrain"/> 계약을 실제로 실행하는 Grain 구현체입니다.
/// </summary>
/// <remarks>
/// Grain은 API가 new로 직접 생성하지 않습니다. API가 Grain 식별자로 참조를 얻어
/// 메서드를 호출하면, Orleans Silo가 이 구현체를 필요할 때 활성화(Activation)합니다.
/// 이 클래스는 3주차의 첫 연결 검증용이며 DB 데이터를 읽거나 변경하지 않습니다.
/// </remarks>
public sealed partial class PingGrain(ILogger<PingGrain> logger) : Grain, IPingGrain
{
    private readonly ILogger<PingGrain> _logger = logger;

    /// <summary>
    /// 호출된 Grain의 식별자와 현재 UTC 시각을 반환합니다.
    /// </summary>
    /// <returns>API가 HTTP 응답으로 변환할 수 있는 진단 결과입니다.</returns>
    public Task<PingGrainResponse> PingAsync()
    {
        // IGrainWithStringKey를 구현했으므로 Orleans가 부여한 문자열 키를 읽습니다.
        // 예: API가 GetGrain<IPingGrain>("local-smoke-test")를 호출하면 이 값은
        // "local-smoke-test"가 됩니다.
        var grainId = this.GetPrimaryKeyString();
        var respondedAtUtc = DateTimeOffset.UtcNow;

        // 구조화 로그의 {GrainId}와 {RespondedAtUtc}는 문자열 연결이 아니라
        // 검색 가능한 로그 속성으로 기록됩니다. 3주차 후반의 traceId·partyId 로그의 기반입니다.
        LogPingResponse(_logger, grainId, respondedAtUtc);

        return Task.FromResult(new PingGrainResponse(grainId, respondedAtUtc));
    }

    /// <summary>
    /// LoggerMessage 소스 생성기가 만드는 고성능 구조화 로그 메서드입니다.
    /// </summary>
    /// <remarks>
    /// 일반 LogInformation의 params 배열 할당 대신 컴파일 시 생성된 코드를 사용합니다.
    /// 따라서 로그 수준이 Information보다 높아 비활성화된 경우, 불필요한 문자열 처리와
    /// 객체 배열 생성을 줄일 수 있습니다.
    /// </remarks>
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Ping Grain responded. GrainId: {GrainId}, RespondedAtUtc: {RespondedAtUtc}")]
    private static partial void LogPingResponse(
        ILogger logger,
        string grainId,
        DateTimeOffset respondedAtUtc);
}
