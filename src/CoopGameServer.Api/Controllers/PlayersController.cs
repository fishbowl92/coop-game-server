using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Players;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.Domain.Players;
using Microsoft.AspNetCore.Authorization;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.Api.Controllers;

/// <summary>
/// 플레이어 생성과 조회를 위한 HTTP API를 제공합니다.
/// </summary>
[ApiController]
[Route("api/players")]
public sealed class PlayersController : ControllerBase
{
    private readonly GameDbContext _gameDbContext;

    /// <summary>
    /// 요청 범위의 데이터베이스 작업 객체를 주입받습니다.
    /// </summary>
    /// <param name="gameDbContext">players 테이블을 읽고 쓰는 EF Core 작업 객체입니다.</param>
    public PlayersController(GameDbContext gameDbContext)
    {
        _gameDbContext = gameDbContext;
    }

    /// <summary>
    /// 새 플레이어를 생성하고 PostgreSQL에 저장합니다.
    /// </summary>
    /// <param name="request">클라이언트가 전송한 닉네임 요청입니다.</param>
    /// <param name="cancellationToken">클라이언트 연결이 끊길 때 DB 작업을 취소하기 위한 토큰입니다.</param>
    /// <returns>성공 시 201 Created와 생성된 플레이어 정보를 반환합니다.</returns>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdministratorOnly)]
    [ProducesResponseType(typeof(PlayerResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PlayerResponse>> CreatePlayer(
        [FromBody] CreatePlayerRequest request,
        CancellationToken cancellationToken)
    {
        Player player;

        try
        {
            // Guid는 서버가 생성해 클라이언트가 다른 플레이어의 식별자를 임의로 지정하지 못하게 합니다.
            player = new Player(Guid.NewGuid(), request.Nickname!, DateTimeOffset.UtcNow);
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

        _gameDbContext.Players.Add(player);

        try
        {
            await _gameDbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateNickname(exception))
        {
            // 사전 조회만으로는 동시에 들어온 두 요청의 경합을 막을 수 없습니다.
            // 따라서 PostgreSQL의 UNIQUE 인덱스 오류를 409 Conflict로 변환합니다.
            return Conflict(new ProblemDetails
            {
                Title = "Nickname already exists.",
                Detail = "이미 사용 중인 닉네임입니다.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        var response = ToResponse(player);

        // 201 Created 응답에는 새 리소스를 다시 조회할 수 있는 Location 헤더를 함께 제공합니다.
        return CreatedAtAction(nameof(GetPlayerById), new { playerId = player.Id }, response);
    }

    /// <summary>
    /// 플레이어 식별자로 저장된 플레이어 한 명을 조회합니다.
    /// </summary>
    /// <param name="playerId">조회할 플레이어의 Guid 식별자입니다.</param>
    /// <param name="cancellationToken">클라이언트 연결이 끊길 때 조회를 취소하기 위한 토큰입니다.</param>
    /// <returns>플레이어가 있으면 200 OK, 없으면 404 Not Found를 반환합니다.</returns>
    [HttpGet("{playerId:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(PlayerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PlayerResponse>> GetPlayerById(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        // 관리자는 운영 목적으로 조회할 수 있지만, 일반 Player는 자기 식별자만 조회할 수 있습니다.
        if (!User.CanAccessPlayer(playerId))
        {
            return Forbid();
        }

        // 조회 결과를 수정하지 않으므로 Change Tracker가 객체 상태를 관리할 필요가 없습니다.
        // AsNoTracking은 이 읽기 전용 경로의 메모리 사용량과 추적 비용을 줄입니다.
        var player = await _gameDbContext.Players
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == playerId, cancellationToken);

        return player is null ? NotFound() : Ok(ToResponse(player));
    }

    /// <summary>
    /// 기존 플레이어의 닉네임을 변경하고 수정 시각을 갱신합니다.
    /// </summary>
    /// <param name="playerId">변경할 플레이어의 Guid 식별자입니다.</param>
    /// <param name="request">새 닉네임을 담은 요청입니다.</param>
    /// <param name="cancellationToken">클라이언트 연결이 끊길 때 DB 작업을 취소하기 위한 토큰입니다.</param>
    /// <returns>
    /// 성공 시 200 OK와 수정된 플레이어 정보를 반환합니다.
    /// 플레이어가 없으면 404, 닉네임 규칙 위반이면 400, 중복 닉네임이면 409를 반환합니다.
    /// </returns>
    [HttpPatch("{playerId:guid}/nickname")]
    [Authorize]
    [ProducesResponseType(typeof(PlayerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PlayerResponse>> UpdatePlayerNickname(
        Guid playerId,
        [FromBody] UpdatePlayerNicknameRequest request,
        CancellationToken cancellationToken)
    {
        // URL의 playerId가 토큰 안 PlayerId와 다르면 다른 사람의 닉네임 변경 시도이므로 막습니다.
        if (!User.CanAccessPlayer(playerId))
        {
            return Forbid();
        }

        // 변경 작업에서는 EF Core가 Player 상태를 추적해야 SaveChangesAsync가 UPDATE SQL을 생성할 수 있습니다.
        // 따라서 단순 조회 API와 달리 AsNoTracking을 사용하지 않습니다.
        var player = await _gameDbContext.Players
            .SingleOrDefaultAsync(entity => entity.Id == playerId, cancellationToken);

        if (player is null)
        {
            return NotFound();
        }

        try
        {
            // Player가 공백 제거·길이 제한·수정 시각 갱신 규칙을 한 곳에서 책임집니다.
            player.Rename(request.Nickname!, DateTimeOffset.UtcNow);
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

        try
        {
            await _gameDbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateNickname(exception))
        {
            // 데이터베이스 UNIQUE 인덱스가 최종적으로 막은 동시 중복 변경을 HTTP 409로 표현합니다.
            return Conflict(new ProblemDetails
            {
                Title = "Nickname already exists.",
                Detail = "이미 사용 중인 닉네임입니다.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        return Ok(ToResponse(player));
    }

    /// <summary>
    /// PostgreSQL이 닉네임 UNIQUE 인덱스 위반으로 반환한 오류인지 판별합니다.
    /// </summary>
    private static bool IsDuplicateNickname(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };
    }

    /// <summary>
    /// DB 도메인 엔티티를 외부에 노출할 API 응답 형식으로 변환합니다.
    /// </summary>
    private static PlayerResponse ToResponse(Player player)
    {
        return new PlayerResponse(player.Id, player.Nickname, player.CreatedAt, player.UpdatedAt);
    }
}
