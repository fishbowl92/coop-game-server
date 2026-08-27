using CoopGameServer.Api.Application.GameRooms;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.GameRooms;
using CoopGameServer.GrainContracts.GameRooms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.Api.Controllers;

/// <summary>매칭으로 생성된 게임 방의 조회와 최소 시작·완료 생명주기를 노출합니다.</summary>
[ApiController]
[Authorize]
[Route("api/game-rooms")]
public sealed class GameRoomsController(GameRoomService gameRoomService) : ControllerBase
{
    /// <summary>방 참가자 또는 관리자가 현재 게임 방 스냅샷을 조회합니다.</summary>
    [HttpGet("{roomId:guid}")]
    [ProducesResponseType(typeof(GameRoomResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GameRoomResponse>> Get(
        Guid roomId,
        CancellationToken cancellationToken)
    {
        var room = await gameRoomService.GetAsync(roomId, cancellationToken);
        if (room is null)
        {
            return NotFound();
        }

        if (!room.PlayerIds.Any(User.CanAccessPlayer))
        {
            return Forbid();
        }

        return Ok(ToResponse(room, isReplay: false));
    }

    /// <summary>
    /// Ready 방을 시작합니다. 아직 전투 명령이 없는 3주차에서는 내부 서버 동작을 대신해 관리자만 호출합니다.
    /// </summary>
    [HttpPost("{roomId:guid}/start")]
    [Authorize(Policy = AuthorizationPolicies.AdministratorOnly)]
    [ProducesResponseType(typeof(GameRoomResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GameRoomResponse>> Start(
        Guid roomId,
        [FromBody] GameRoomCommandRequest request,
        CancellationToken cancellationToken)
    {
        var result = await gameRoomService.StartAsync(roomId, request.RequestId, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>
    /// 게임 방을 완료하고 사전 구성 파티를 유지한 채 로비로 돌려보냅니다.
    /// 실제 전투 결과·보상 검증이 추가되기 전까지 관리자 전용입니다.
    /// </summary>
    [HttpPost("{roomId:guid}/complete")]
    [Authorize(Policy = AuthorizationPolicies.AdministratorOnly)]
    [ProducesResponseType(typeof(GameRoomResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GameRoomResponse>> Complete(
        Guid roomId,
        [FromBody] CompleteGameRoomRequest request,
        CancellationToken cancellationToken)
    {
        var result = await gameRoomService.CompleteAsync(
            roomId,
            request.RequestId,
            request.Outcome,
            cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>GameRoomGrain의 업무 오류를 외부 HTTP 오류로 변환합니다.</summary>
    private ActionResult<GameRoomResponse> ToActionResult(GameRoomCommandResult result)
    {
        if (result.Error is GameRoomCommandError.None)
        {
            return Ok(ToResponse(
                result.Room ?? throw new InvalidOperationException("성공한 게임 방 명령에 스냅샷이 없습니다."),
                result.IsReplay));
        }

        var (statusCode, title) = result.Error switch
        {
            GameRoomCommandError.InvalidRequestId
                or GameRoomCommandError.InvalidRoomId
                or GameRoomCommandError.InvalidOutcome
                => (StatusCodes.Status400BadRequest, "Invalid game room request."),
            GameRoomCommandError.RoomNotCreated
                => (StatusCodes.Status404NotFound, "Game room was not found."),
            _ => (StatusCodes.Status409Conflict, "Game room command conflicts with the current state."),
        };

        return StatusCode(statusCode, new ProblemDetails
        {
            Title = title,
            Detail = result.PartyError is null
                ? $"Game room command failed with error '{result.Error}'."
                : $"Game room command failed for party '{result.FailedPartyId}' with error '{result.PartyError}'.",
            Status = statusCode,
        });
    }

    private static GameRoomResponse ToResponse(GameRoomSnapshot room, bool isReplay)
    {
        return new GameRoomResponse(
            room.RoomId,
            room.QueueKey,
            room.Lifecycle.ToString(),
            room.PartyIds.ToArray(),
            room.PlayerIds.ToArray(),
            room.CreatedAt,
            room.StartedAt,
            room.CompletedAt,
            room.Outcome.ToString(),
            room.RewardPolicyVersion,
            isReplay);
    }
}
