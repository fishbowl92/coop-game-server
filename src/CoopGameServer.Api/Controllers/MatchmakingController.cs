using CoopGameServer.Api.Application.Matchmaking;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Matchmaking;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.Api.Controllers;

/// <summary>
/// 인증된 플레이어의 매칭 신청·조회·취소를 내부 Orleans 대기열 명령으로 변환합니다.
/// </summary>
/// <remarks>
/// 플레이어·파티 멤버 배열은 HTTP 본문에서 신뢰하지 않습니다.
/// JWT의 Player ID와 PartyGrain의 최신 상태로 서버가 내부 요청을 다시 구성합니다.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/matchmaking/queues/{queueKey}")]
public sealed class MatchmakingController(MatchmakingService matchmakingService) : ControllerBase
{
    /// <summary>현재 인증된 플레이어 한 명을 솔로 티켓으로 등록합니다.</summary>
    [HttpPost("solo")]
    [ProducesResponseType(typeof(MatchmakingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MatchmakingResponse>> EnqueueSolo(
        string queueKey,
        [FromBody] EnqueueMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetPlayerId(out var playerId))
        {
            return Unauthorized();
        }

        var result = await matchmakingService.EnqueueSoloAsync(
            queueKey,
            request.RequestId,
            playerId,
            cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>현재 리더가 속한 사전 구성 파티 전체를 한 티켓으로 등록합니다.</summary>
    [HttpPost("parties/{partyId:guid}")]
    [ProducesResponseType(typeof(MatchmakingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MatchmakingResponse>> EnqueueParty(
        string queueKey,
        Guid partyId,
        [FromBody] EnqueueMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetPlayerId(out var requesterPlayerId))
        {
            return Unauthorized();
        }

        var result = await matchmakingService.EnqueuePartyAsync(
            queueKey,
            partyId,
            request.RequestId,
            requesterPlayerId,
            User.IsInRole(AccountRole.Administrator.ToString()),
            cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>아직 대기 중인 티켓을 취소하고 사전 구성 파티라면 멤버 잠금을 풉니다.</summary>
    [HttpPost("tickets/{ticketId:guid}/cancel")]
    [ProducesResponseType(typeof(MatchmakingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MatchmakingResponse>> Cancel(
        string queueKey,
        Guid ticketId,
        [FromBody] CancelMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetPlayerId(out var requesterPlayerId))
        {
            return Unauthorized();
        }

        var result = await matchmakingService.CancelAsync(
            queueKey,
            ticketId,
            request.RequestId,
            requesterPlayerId,
            User.IsInRole(AccountRole.Administrator.ToString()),
            cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>현재 플레이어가 포함된 매칭 티켓의 상태와 배정된 roomId를 조회합니다.</summary>
    [HttpGet("tickets/{ticketId:guid}")]
    [ProducesResponseType(typeof(MatchQueueTicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MatchQueueTicketResponse>> GetTicket(
        string queueKey,
        Guid ticketId,
        CancellationToken cancellationToken)
    {
        var ticket = await matchmakingService.GetTicketAsync(queueKey, ticketId, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (!ticket.MemberPlayerIds.Any(User.CanAccessPlayer))
        {
            return Forbid();
        }

        return Ok(ToResponse(ticket));
    }

    /// <summary>애플리케이션·파티·대기열 오류를 일관된 HTTP 상태 코드로 변환합니다.</summary>
    private ActionResult<MatchmakingResponse> ToActionResult(MatchmakingApplicationResult result)
    {
        if (result.Error is not MatchmakingApplicationError.None)
        {
            return ToApplicationError(result.Error, result.PartyError);
        }

        var queueResult = result.QueueResult
            ?? throw new InvalidOperationException("성공한 매칭 애플리케이션 결과에 대기열 결과가 없습니다.");
        if (queueResult.Error is not MatchQueueCommandError.None)
        {
            return ToQueueError(queueResult.Error);
        }

        var ticket = queueResult.Ticket
            ?? throw new InvalidOperationException("성공한 매칭 명령에 티켓이 없습니다.");
        return Ok(new MatchmakingResponse(
            queueResult.IsReplay,
            ToResponse(ticket),
            queueResult.Match is null ? null : ToResponse(queueResult.Match)));
    }

    private ActionResult<MatchmakingResponse> ToApplicationError(
        MatchmakingApplicationError error,
        PartyCommandError? partyError)
    {
        var (statusCode, title) = error switch
        {
            MatchmakingApplicationError.InvalidQueueKey
                => (StatusCodes.Status400BadRequest, "Invalid matchmaking queue key."),
            MatchmakingApplicationError.PartyNotFound
                => (StatusCodes.Status404NotFound, "Party was not found."),
            MatchmakingApplicationError.RequesterIsNotPartyLeader
                or MatchmakingApplicationError.RequesterCannotManageTicket
                => (StatusCodes.Status403Forbidden, "The requester cannot manage this matchmaking entry."),
            MatchmakingApplicationError.SoloPlayerAlreadyInParty
                => (StatusCodes.Status409Conflict, "A party member cannot queue as a solo player."),
            _ => (StatusCodes.Status409Conflict, "Party and matchmaking state could not be synchronized."),
        };

        return StatusCode(statusCode, new ProblemDetails
        {
            Title = title,
            Detail = partyError is null
                ? $"Matchmaking request failed with error '{error}'."
                : $"Matchmaking request failed with error '{error}' and party error '{partyError}'.",
            Status = statusCode,
        });
    }

    private ActionResult<MatchmakingResponse> ToQueueError(MatchQueueCommandError error)
    {
        var (statusCode, title) = error switch
        {
            MatchQueueCommandError.InvalidRequestId
                or MatchQueueCommandError.InvalidPartyId
                or MatchQueueCommandError.InvalidLeaderPlayerId
                or MatchQueueCommandError.InvalidMembers
                or MatchQueueCommandError.LeaderNotMember
                or MatchQueueCommandError.InvalidEntryShape
                => (StatusCodes.Status400BadRequest, "Invalid matchmaking request."),
            MatchQueueCommandError.TicketNotFound
                => (StatusCodes.Status404NotFound, "Matchmaking ticket was not found."),
            MatchQueueCommandError.OnlyLeaderCanCancel
                => (StatusCodes.Status403Forbidden, "Only the ticket leader can cancel matchmaking."),
            _ => (StatusCodes.Status409Conflict, "Matchmaking request conflicts with the current state."),
        };

        return StatusCode(statusCode, new ProblemDetails
        {
            Title = title,
            Detail = $"Matchmaking command failed with error '{error}'.",
            Status = statusCode,
        });
    }

    private static MatchQueueTicketResponse ToResponse(MatchQueueTicket ticket)
    {
        return new MatchQueueTicketResponse(
            ticket.TicketId,
            ticket.QueueKey,
            ticket.EntryKind.ToString(),
            ticket.PartyId,
            ticket.LeaderPlayerId,
            ticket.MemberPlayerIds.ToArray(),
            ticket.Status.ToString(),
            ticket.RoomId,
            ticket.EnqueuedAt,
            ticket.QueueOrder);
    }

    private static MatchAssignmentResponse ToResponse(MatchAssignment match)
    {
        return new MatchAssignmentResponse(
            match.RoomId,
            match.QueueKey,
            match.PartyIds.ToArray(),
            match.PlayerIds.ToArray(),
            match.CreatedAt);
    }
}
