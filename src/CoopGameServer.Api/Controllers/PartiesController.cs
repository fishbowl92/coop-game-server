using CoopGameServer.Api.Authentication;
using CoopGameServer.Api.Application.Parties;
using CoopGameServer.Contracts.Parties;
using CoopGameServer.GrainContracts.Parties;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.Api.Controllers;

/// <summary>
/// HTTP 파티 요청을 Orleans PartyGrain 명령으로 변환합니다.
/// </summary>
/// <remarks>
/// Controller는 URL·요청 본문·HTTP 상태 코드만 담당합니다.
/// 최대 인원·리더 승계·멱등성·영속성 같은 게임 규칙은 PartyGrain에 남겨
/// HTTP가 아닌 다른 호출 경로에서도 같은 규칙을 재사용할 수 있게 합니다.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/parties")]
public sealed class PartiesController(PartyService partyService) : ControllerBase
{
    /// <summary>
    /// 서버가 새 partyId를 만들고 요청의 플레이어를 첫 리더로 지정합니다.
    /// </summary>
    /// <returns>최초 생성은 201, 같은 요청의 재전송은 200을 반환합니다.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(PartyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PartyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PartyResponse>> CreateParty(
        [FromBody] CreatePartyRequest request,
        CancellationToken cancellationToken)
    {
        // 생성 요청의 리더는 토큰의 본인 Player와 같아야 합니다.
        // 클라이언트가 다른 PlayerId를 본문에 넣어 대신 파티를 만들 수 없게 합니다.
        if (!User.CanAccessPlayer(request.LeaderPlayerId))
        {
            return Forbid();
        }

        var result = await partyService.CreateAsync(
            request.RequestId,
            request.LeaderPlayerId,
            cancellationToken);

        if (result.Error is not PartyCommandError.None)
        {
            return ToErrorResult(result.Error);
        }

        var response = ToResponse(result);

        // 같은 requestId의 재시도는 새 리소스를 만들지 않았으므로 200 OK를 사용합니다.
        // 최초 생성만 201 Created와 조회 가능한 Location 헤더를 반환합니다.
        return result.IsReplay
            ? Ok(response)
            : CreatedAtAction(
                nameof(GetPartyById),
                new { partyId = response.PartyId },
                response);
    }

    /// <summary>partyId에 해당하는 현재 파티 상태를 조회합니다.</summary>
    [HttpGet("{partyId:guid}")]
    [ProducesResponseType(typeof(PartyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PartyResponse>> GetPartyById(
        Guid partyId,
        CancellationToken cancellationToken)
    {
        var snapshot = await partyService.GetAsync(partyId, cancellationToken);

        if (snapshot is null)
        {
            return NotFound();
        }

        // 파티 정보에는 멤버 식별자가 포함되므로 현재 멤버 또는 관리자로 조회를 제한합니다.
        if (!snapshot.MemberPlayerIds.Any(User.CanAccessPlayer))
        {
            return Forbid();
        }

        return Ok(ToResponse(snapshot, isReplay: false));
    }

    /// <summary>기존 파티에 플레이어 한 명을 가입시킵니다.</summary>
    [HttpPost("{partyId:guid}/members")]
    [ProducesResponseType(typeof(PartyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PartyResponse>> JoinParty(
        Guid partyId,
        [FromBody] JoinPartyRequest request,
        CancellationToken cancellationToken)
    {
        // 가입은 "내가 이 파티에 가입한다"는 자기 자신에 대한 요청만 허용합니다.
        if (!User.CanAccessPlayer(request.PlayerId))
        {
            return Forbid();
        }

        var result = await partyService.JoinAsync(
            partyId,
            request.RequestId,
            request.PlayerId,
            cancellationToken);

        return ToActionResult(result);
    }

    /// <summary>
    /// 플레이어 한 명을 파티에서 탈퇴시킵니다.
    /// 리더가 탈퇴하면 PartyGrain이 가입 순서에 따라 새 리더를 선정합니다.
    /// </summary>
    [HttpPost("{partyId:guid}/leave")]
    [ProducesResponseType(typeof(PartyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PartyResponse>> LeaveParty(
        Guid partyId,
        [FromBody] LeavePartyRequest request,
        CancellationToken cancellationToken)
    {
        // 탈퇴도 임의의 다른 멤버를 강제로 내보내지 못하도록 본인 요청으로 제한합니다.
        if (!User.CanAccessPlayer(request.PlayerId))
        {
            return Forbid();
        }

        var result = await partyService.LeaveAsync(
            partyId,
            request.RequestId,
            request.PlayerId,
            cancellationToken);

        return ToActionResult(result);
    }

    /// <summary>현재 리더의 요청으로 파티를 명시적으로 해산합니다.</summary>
    [HttpPost("{partyId:guid}/disband")]
    [ProducesResponseType(typeof(PartyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PartyResponse>> DisbandParty(
        Guid partyId,
        [FromBody] DisbandPartyRequest request,
        CancellationToken cancellationToken)
    {
        // 실제 리더인지 여부는 PartyGrain이 확인하고, 여기서는 먼저 요청자가 본인 ID를 위조하지 않았는지 확인합니다.
        if (!User.CanAccessPlayer(request.LeaderPlayerId))
        {
            return Forbid();
        }

        var result = await partyService.DisbandAsync(
            partyId,
            request.RequestId,
            request.LeaderPlayerId,
            cancellationToken);

        return ToActionResult(result);
    }

    /// <summary>성공 결과는 200, 업무 오류는 해당 HTTP 오류 응답으로 변환합니다.</summary>
    private ActionResult<PartyResponse> ToActionResult(PartyCommandResult result)
    {
        return result.Error is PartyCommandError.None
            ? Ok(ToResponse(result))
            : ToErrorResult(result.Error);
    }

    /// <summary>
    /// Grain의 명시적 업무 오류를 HTTP 상태 코드로 변환합니다.
    /// </summary>
    /// <remarks>
    /// 입력 자체가 잘못되면 400, 대상이 없으면 404, 권한 규칙 위반은 403,
    /// 현재 상태와 충돌하면 409를 사용합니다.
    /// </remarks>
    private ActionResult<PartyResponse> ToErrorResult(PartyCommandError error)
    {
        var (statusCode, title) = error switch
        {
            PartyCommandError.InvalidPartyId
                or PartyCommandError.InvalidRequestId
                or PartyCommandError.InvalidPlayerId
                => (StatusCodes.Status400BadRequest, "Invalid party request."),

            PartyCommandError.PlayerNotFound
                or PartyCommandError.PartyNotCreated
                or PartyCommandError.MemberNotFound
                => (StatusCodes.Status404NotFound, "Party resource was not found."),

            PartyCommandError.OnlyLeaderCanDisband
                => (StatusCodes.Status403Forbidden, "Only the current leader can disband the party."),

            _ => (StatusCodes.Status409Conflict, "Party command conflicts with the current state."),
        };

        return StatusCode(statusCode, new ProblemDetails
        {
            Title = title,
            Detail = $"Party command failed with error '{error}'.",
            Status = statusCode,
        });
    }

    /// <summary>Grain 명령 결과에 재실행 여부를 포함한 HTTP 응답을 만듭니다.</summary>
    private static PartyResponse ToResponse(PartyCommandResult result)
    {
        return ToResponse(result.Party!, result.IsReplay);
    }

    /// <summary>Orleans 전송 모델을 외부 HTTP 계약으로 복사합니다.</summary>
    private static PartyResponse ToResponse(PartySnapshot snapshot, bool isReplay)
    {
        return new PartyResponse(
            snapshot.PartyId,
            snapshot.Lifecycle.ToString(),
            snapshot.LeaderPlayerId,
            snapshot.MemberPlayerIds.ToArray(),
            snapshot.CurrentRoomId,
            isReplay);
    }
}
