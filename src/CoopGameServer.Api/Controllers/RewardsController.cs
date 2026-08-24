using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.Persistence.Rewards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.Api.Controllers;

/// <summary>
/// 플레이어 보상을 지급하고 멱등성 재시도를 처리하는 HTTP API를 제공합니다.
/// </summary>
[ApiController]
[Route("api/players/{playerId:guid}/rewards")]
public sealed class RewardsController : ControllerBase
{
    private readonly RewardService _rewardService;

    /// <summary>
    /// 보상 지급 서비스와 컨트롤러를 연결합니다.
    /// </summary>
    /// <param name="rewardService">HTTP 요청을 영속성 명령으로 변환하는 임시 API 어댑터입니다.</param>
    public RewardsController(RewardService rewardService)
    {
        _rewardService = rewardService;
    }

    /// <summary>
    /// 한 플레이어에게 골드와 선택적 아이템을 지급합니다.
    /// </summary>
    /// <param name="playerId">보상을 받을 플레이어 식별자입니다.</param>
    /// <param name="request">멱등성 키와 보상 내용을 담은 요청 본문입니다.</param>
    /// <param name="cancellationToken">
    /// 작업 시작 전에 클라이언트 요청이 이미 취소됐는지 확인하는 토큰입니다.
    /// 이미 시작한 멱등성 보상 기록은 서버가 결과를 확정할 때까지 기다립니다.
    /// </param>
    /// <returns>
    /// 신규 보상 적용 시 201 Created, 같은 요청의 재시도면 200 OK를 반환합니다.
    /// 유효하지 않은 입력은 400, 없는 플레이어는 404, 다른 내용으로 키를 재사용하면 409를 반환합니다.
    /// </returns>
    [HttpPost]
    // 보상 액수·아이템 종류는 일반 사용자가 정할 수 있는 값이 아닙니다.
    // 현재 단계에서는 관리자 전용으로 막고, 이후 신뢰된 서버 이벤트·보상 테이블로 입력 출처를 좁힙니다.
    [Authorize(Policy = AuthorizationPolicies.AdministratorOnly)]
    [ProducesResponseType(typeof(GrantRewardResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(GrantRewardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GrantRewardResponse>> GrantReward(
        Guid playerId,
        [FromBody] GrantRewardRequest request,
        CancellationToken cancellationToken)
    {
        GrantRewardResult? result;

        try
        {
            result = await _rewardService.GrantAsync(playerId, request, cancellationToken);
        }
        catch (ArgumentNullException exception)
        {
            return ValidationProblem(
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblem(
                detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (IdempotencyKeyConflictException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Idempotency key was reused with different reward data.",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict,
            });
        }

        if (result is null)
        {
            return NotFound();
        }

        var response = ToResponse(result.Receipt, result.IsReplay);

        // 재전송은 새 리소스를 만들지 않았으므로 200, 최초 적용은 새 보상 이력을 만들었으므로 201을 사용합니다.
        return result.IsReplay
            ? Ok(response)
            : StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>
    /// Persistence 영수증을 외부 API 응답 형식으로 변환합니다.
    /// </summary>
    private static GrantRewardResponse ToResponse(RewardWriteReceipt receipt, bool isReplay)
    {
        return new GrantRewardResponse(
            receipt.RewardAuditId,
            receipt.RequestId,
            receipt.PlayerId,
            receipt.GoldAmount,
            receipt.ItemId,
            receipt.ItemQuantity,
            receipt.Reason,
            receipt.CreatedAt,
            isReplay);
    }
}
