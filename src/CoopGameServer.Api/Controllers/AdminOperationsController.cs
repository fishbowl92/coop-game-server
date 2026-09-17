using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Administration;
using CoopGameServer.Domain.Players;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Api.Controllers;

/// <summary>운영자가 플레이어를 찾고 최근 보상 이력을 확인하는 읽기 전용 API입니다.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = AuthorizationPolicies.AdministratorOnly)]
public sealed class AdminOperationsController(GameDbContext gameDbContext) : ControllerBase
{
    /// <summary>Guid 또는 완전히 일치하는 닉네임으로 플레이어 한 명을 찾습니다.</summary>
    [HttpGet("players/lookup")]
    [ProducesResponseType(typeof(AdminPlayerLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminPlayerLookupResponse>> LookupPlayer(
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim();
        if (string.IsNullOrEmpty(normalizedQuery))
        {
            return ValidationProblem(
                detail: $"query는 Player Guid 또는 1자 이상 {Player.MaxNicknameLength}자 이하의 정확한 닉네임이어야 합니다.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        Player? player;
        if (Guid.TryParse(normalizedQuery, out var playerId))
        {
            player = await gameDbContext.Players
                .AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.Id == playerId, cancellationToken);
        }
        else
        {
            if (normalizedQuery.Length > Player.MaxNicknameLength)
            {
                return ValidationProblem(
                    detail: $"닉네임은 {Player.MaxNicknameLength}자 이하여야 합니다.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            player = await gameDbContext.Players
                .AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.Nickname == normalizedQuery, cancellationToken);
        }

        return player is null
            ? NotFound()
            : Ok(new AdminPlayerLookupResponse(
                player.Id,
                player.Nickname,
                player.CreatedAt,
                player.UpdatedAt));
    }

    /// <summary>한 플레이어의 최근 보상과 관리자 실행 주체를 최신순으로 조회합니다.</summary>
    [HttpGet("players/{playerId:guid}/reward-history")]
    [ProducesResponseType(typeof(AdminRewardHistoryResponse[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminRewardHistoryResponse[]>> GetRewardHistory(
        Guid playerId,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            return ValidationProblem(
                detail: "limit는 1 이상 100 이하여야 합니다.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await gameDbContext.Players.AsNoTracking().AnyAsync(
                player => player.Id == playerId,
                cancellationToken))
        {
            return NotFound();
        }

        // 게임 완료 보상에는 관리자 감사 행이 없을 수 있으므로 LEFT JOIN으로 모두 보존합니다.
        var history = await (
                from reward in gameDbContext.RewardAudits.AsNoTracking()
                where reward.PlayerId == playerId
                join adminAudit in gameDbContext.AdminAudits.AsNoTracking()
                    on reward.RequestId equals adminAudit.RequestId into adminAudits
                from adminAudit in adminAudits.DefaultIfEmpty()
                join account in gameDbContext.Accounts.AsNoTracking()
                    on adminAudit.AdministratorAccountId equals account.Id into accounts
                from account in accounts.DefaultIfEmpty()
                orderby reward.CreatedAt descending, reward.Id descending
                select new AdminRewardHistoryResponse(
                    reward.Id,
                    reward.RequestId,
                    reward.PlayerId,
                    reward.GoldAmount,
                    reward.ItemId,
                    reward.ItemQuantity,
                    reward.Reason,
                    reward.CreatedAt,
                    adminAudit == null ? null : adminAudit.AdministratorAccountId,
                    account == null ? null : account.LoginId))
            .Take(limit)
            .ToArrayAsync(cancellationToken);

        return Ok(history);
    }
}
