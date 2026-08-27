using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.GrainContracts.Players;
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
    /// <param name="rewardService">HTTP 요청을 PlayerGrain 명령으로 변환하는 API 어댑터입니다.</param>
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
    /// 호출 전 취소는 Grain 시작을 막고, 호출 뒤 취소는 HTTP 응답 대기만 중단합니다.
    /// 이미 시작한 Grain 보상 처리는 Silo에서 결과를 끝까지 확정합니다.
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
        var result = await _rewardService.GrantAsync(playerId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// PlayerGrain의 업무 결과를 기존 보상 HTTP 상태와 응답 형식으로 변환합니다.
    /// </summary>
    private ActionResult<GrantRewardResponse> ToActionResult(PlayerRewardCommandResult result)
    {
        if (result.Status is PlayerRewardCommandStatus.Applied &&
            result.Error is PlayerRewardCommandError.None)
        {
            var receipt = result.Receipt
                ?? throw new InvalidOperationException("적용된 PlayerGrain 보상 결과에 영수증이 없습니다.");
            var response = ToResponse(receipt, result.IsReplay);

            // 재전송은 새 이력을 만들지 않았으므로 200, 최초 적용은 이력을 새로 만들었으므로 201입니다.
            return result.IsReplay
                ? Ok(response)
                : StatusCode(StatusCodes.Status201Created, response);
        }

        // 관리자 명령의 예상 거부 결과는 재생 영수증을 포함하면 안 됩니다.
        if (result.Status is not PlayerRewardCommandStatus.Rejected ||
            result.IsReplay ||
            result.Receipt is not null)
        {
            throw new InvalidOperationException(
                $"관리자 보상 결과 조합이 유효하지 않습니다: Status={result.Status}, Error={result.Error}");
        }

        return result.Error switch
        {
            PlayerRewardCommandError.InvalidRequest => ValidationProblem(
                detail: "Reward request values are invalid.",
                statusCode: StatusCodes.Status400BadRequest),
            PlayerRewardCommandError.PlayerNotFound => NotFound(),
            PlayerRewardCommandError.IdempotencyConflict => Conflict(new ProblemDetails
            {
                Title = "Idempotency key was reused with different reward data.",
                Detail = "The request ID has already been used with different reward data.",
                Status = StatusCodes.Status409Conflict,
            }),
            _ => throw new InvalidOperationException(
                $"관리자 보상 API에서 지원하지 않는 PlayerGrain 오류입니다: {result.Error}"),
        };
    }

    /// <summary>Orleans 보상 영수증을 외부 API 응답 형식으로 복사합니다.</summary>
    private static GrantRewardResponse ToResponse(PlayerRewardReceipt receipt, bool isReplay)
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
