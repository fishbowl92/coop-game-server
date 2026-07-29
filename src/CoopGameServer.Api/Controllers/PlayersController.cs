using CoopGameServer.Api.Data;
using CoopGameServer.Api.Domain.Players;
using CoopGameServer.Contracts.Players;
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
    [ProducesResponseType(typeof(PlayerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlayerResponse>> GetPlayerById(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        // 조회 결과를 수정하지 않으므로 Change Tracker가 객체 상태를 관리할 필요가 없습니다.
        // AsNoTracking은 이 읽기 전용 경로의 메모리 사용량과 추적 비용을 줄입니다.
        var player = await _gameDbContext.Players
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == playerId, cancellationToken);

        return player is null ? NotFound() : Ok(ToResponse(player));
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
