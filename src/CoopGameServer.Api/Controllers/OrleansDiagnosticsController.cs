using CoopGameServer.Api.Authentication;
using CoopGameServer.GrainContracts.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orleans;
using Orleans.Runtime;

namespace CoopGameServer.Api.Controllers;

/// <summary>
/// API에서 Orleans Silo의 Grain을 호출할 수 있는지 확인하는 진단용 HTTP 엔드포인트입니다.
/// </summary>
/// <remarks>
/// 이 컨트롤러는 3주차 첫 스파이크(작은 기술 연결 실험) 전용입니다.
/// 파티·재화·인벤토리·PostgreSQL 데이터를 변경하지 않으므로, Silo 연결 경로의 문제를
/// 게임 규칙 문제와 분리해서 확인할 수 있습니다.
/// </remarks>
[ApiController]
[Route("api/diagnostics/orleans")]
public sealed class OrleansDiagnosticsController(IGrainFactory grainFactory) : ControllerBase
{
    private readonly IGrainFactory _grainFactory = grainFactory;

    /// <summary>
    /// 지정한 문자열 식별자를 가진 Ping Grain을 호출합니다.
    /// </summary>
    /// <param name="grainId">
    /// 진단용 Grain 식별자입니다. 같은 문자열은 같은 논리적 Grain을 가리킵니다.
    /// </param>
    /// <param name="cancellationToken">
    /// 브라우저 연결 종료 또는 서버 종료 시 Orleans 호출에도 취소 신호를 전달합니다.
    /// </param>
    /// <returns>
    /// Silo가 응답하면 200 OK와 Grain 식별자·UTC 시각을 반환합니다.
    /// Silo에 연결할 수 없으면 503 Service Unavailable을 반환합니다.
    /// </returns>
    [HttpGet("ping/{grainId}")]
    // 외부 사용자가 임의 Grain을 계속 활성화하지 못하도록 진단 호출은 운영자에게만 허용합니다.
    [Authorize(Policy = AuthorizationPolicies.AdministratorOnly)]
    [ProducesResponseType<PingGrainResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PingGrainResponse>> PingAsync(
        string grainId,
        CancellationToken cancellationToken)
    {
        // Route 값은 문자열이므로 공백만 전달됐을 때 Grain 키로 사용하지 않습니다.
        if (string.IsNullOrWhiteSpace(grainId))
        {
            return ValidationProblem(
                detail: "Grain 식별자는 공백일 수 없습니다.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // GetGrain은 Grain 객체를 직접 만드는 코드가 아닙니다.
        // "이 식별자의 IPingGrain을 호출하고 싶다"는 Orleans 참조를 얻는 동작입니다.
        var pingGrain = _grainFactory.GetGrain<IPingGrain>(grainId.Trim());

        try
        {
            // PingAsync는 API → Orleans Client → Silo → PingGrain 순서로 전달됩니다.
            var response = await pingGrain.PingAsync().WaitAsync(cancellationToken);

            return Ok(response);
        }
        catch (SiloUnavailableException)
        {
            // Silo가 꺼져 있거나 API Client가 아직 연결되지 않은 경우를
            // 서버 내부 오류 500이 아니라 "현재 서비스를 사용할 수 없음"인 503으로 표현합니다.
            return Problem(
                title: "Orleans Silo에 연결할 수 없습니다.",
                detail: "CoopGameServer.Silo가 실행 중인지 확인하세요.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
