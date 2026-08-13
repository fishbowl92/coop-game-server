using CoopGameServer.Contracts.Rewards;
using CoopGameServer.Domain.Inventories;
using CoopGameServer.Domain.Rewards;
using CoopGameServer.Domain.Wallets;
using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>
/// 보상 지급의 멱등성·트랜잭션·지갑·인벤토리 변경을 한곳에서 처리합니다.
/// </summary>
/// <remarks>
/// 컨트롤러는 HTTP 요청과 응답에 집중하고, 이 서비스는 게임 데이터의 일관성에 집중합니다.
/// 보상 이력을 먼저 저장해 멱등성 키를 확보한 뒤 지갑과 인벤토리를 변경합니다.
/// 세 변경은 같은 트랜잭션에 있으므로 중간에 실패하면 모두 롤백(Rollback, 변경을 취소하여 이전 상태로 되돌림)됩니다.
/// </remarks>
public sealed class RewardService
{
    private const string RequestIdUniqueIndexName = "IX_reward_audits_request_id";

    private readonly GameDbContext _gameDbContext;

    /// <summary>
    /// 요청 범위의 EF Core 작업 단위를 주입받습니다.
    /// </summary>
    /// <param name="gameDbContext">보상 관련 테이블을 읽고 쓰는 데이터베이스 작업 객체입니다.</param>
    public RewardService(GameDbContext gameDbContext)
    {
        _gameDbContext = gameDbContext;
    }

    /// <summary>
    /// 한 번의 보상 요청을 처리하거나, 같은 멱등성 키의 기존 결과를 반환합니다.
    /// </summary>
    /// <param name="playerId">보상을 받는 플레이어 식별자입니다.</param>
    /// <param name="request">골드·아이템·멱등성 키·사유를 담은 요청입니다.</param>
    /// <param name="cancellationToken">클라이언트 연결 종료 시 DB 작업을 취소하는 토큰입니다.</param>
    /// <returns>플레이어가 없으면 null, 그 외에는 신규 적용 또는 재전송 결과를 반환합니다.</returns>
    /// <exception cref="ArgumentException">보상 금액·아이템·사유·멱등성 키가 유효하지 않으면 발생합니다.</exception>
    /// <exception cref="IdempotencyKeyConflictException">같은 키가 다른 보상 내용에 이미 사용되면 발생합니다.</exception>
    public async Task<GrantRewardResult?> GrantAsync(
        Guid playerId,
        GrantRewardRequest request,
        CancellationToken cancellationToken)
    {
        // 도메인 객체를 먼저 만들면 API 입력 검증과 DB 검사 제약 조건이 같은 규칙을 따릅니다.
        // Guid.Empty는 누락된 RequestId를 포함해 유효하지 않은 식별자를 일관되게 거부합니다.
        var requestedRewardAudit = new RewardAudit(
            Guid.NewGuid(),
            request.RequestId ?? Guid.Empty,
            playerId,
            request.GoldAmount,
            request.ItemId,
            request.ItemQuantity,
            request.Reason!,
            DateTimeOffset.UtcNow);

        // 일반적인 재시도는 DB를 수정하지 않고, 최초 처리 때 저장한 결과를 그대로 반환합니다.
        var existingRewardAudit = await FindRewardAuditAsync(
            requestedRewardAudit.RequestId,
            cancellationToken);

        if (existingRewardAudit is not null)
        {
            EnsureSameRewardRequest(existingRewardAudit, requestedRewardAudit);
            return new GrantRewardResult(existingRewardAudit, IsReplay: true);
        }

        await using var transaction = await _gameDbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // 같은 플레이어의 서로 다른 정상 보상 요청도 한 번에 하나씩 처리되도록
            // players의 해당 행을 현재 트랜잭션이 끝날 때까지 잠급니다.
            // 지갑이나 인벤토리가 아직 없어도 Player 행은 항상 존재하므로 안정적인 잠금 기준점이 됩니다.
            var playerExists = await TryLockPlayerAsync(playerId, cancellationToken);

            if (!playerExists)
            {
                // 존재하지 않는 플레이어에게는 아무 변경도 남기지 않고 트랜잭션을 종료합니다.
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            // 먼저 request_id UNIQUE 인덱스에 보상 이력을 기록해 같은 요청의 동시 처리를 하나만 통과시킵니다.
            _gameDbContext.RewardAudits.Add(requestedRewardAudit);
            await _gameDbContext.SaveChangesAsync(cancellationToken);

            // 골드가 0인 아이템 전용 보상도 최초 보상 시 지갑 행을 만들어 이후 지갑 조회 구조를 단순하게 합니다.
            var wallet = await _gameDbContext.PlayerWallets
                .SingleOrDefaultAsync(entity => entity.PlayerId == playerId, cancellationToken);

            if (wallet is null)
            {
                wallet = new PlayerWallet(playerId, requestedRewardAudit.CreatedAt);
                _gameDbContext.PlayerWallets.Add(wallet);
            }

            if (requestedRewardAudit.GoldAmount > 0)
            {
                wallet.AddGold(requestedRewardAudit.GoldAmount, requestedRewardAudit.CreatedAt);
            }

            if (requestedRewardAudit.ItemId is int itemId &&
                requestedRewardAudit.ItemQuantity is int itemQuantity)
            {
                var inventoryItem = await _gameDbContext.InventoryItems
                    .SingleOrDefaultAsync(
                        entity => entity.PlayerId == playerId && entity.ItemId == itemId,
                        cancellationToken);

                if (inventoryItem is null)
                {
                    inventoryItem = new InventoryItem(
                        playerId,
                        itemId,
                        itemQuantity,
                        requestedRewardAudit.CreatedAt);
                    _gameDbContext.InventoryItems.Add(inventoryItem);
                }
                else
                {
                    inventoryItem.AddQuantity(itemQuantity, requestedRewardAudit.CreatedAt);
                }
            }

            // 지갑·인벤토리 변경까지 모두 성공해야 보상 이력도 확정됩니다.
            await _gameDbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new GrantRewardResult(requestedRewardAudit, IsReplay: false);
        }
        catch (DbUpdateException exception) when (IsDuplicateRequestId(exception))
        {
            // 다른 요청이 같은 request_id를 먼저 커밋한 경우, 이번 작업은 전부 취소하고 기존 결과를 돌려줍니다.
            await transaction.RollbackAsync(CancellationToken.None);
            _gameDbContext.ChangeTracker.Clear();

            var competingRewardAudit = await FindRewardAuditAsync(
                requestedRewardAudit.RequestId,
                cancellationToken);

            // UNIQUE 위반 직후에는 경쟁 요청이 커밋되어 있어야 합니다. 없으면 예상 밖의 DB 상태이므로 원래 예외를 보존합니다.
            if (competingRewardAudit is null)
            {
                throw;
            }

            EnsureSameRewardRequest(competingRewardAudit, requestedRewardAudit);
            return new GrantRewardResult(competingRewardAudit, IsReplay: true);
        }
    }

    /// <summary>
    /// 멱등성 키로 기존 보상 이력을 읽습니다. 읽기 전용이므로 Change Tracker 추적을 끕니다.
    /// </summary>
    private async Task<RewardAudit?> FindRewardAuditAsync(Guid requestId, CancellationToken cancellationToken)
    {
        return await _gameDbContext.RewardAudits
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.RequestId == requestId, cancellationToken);
    }

    /// <summary>
    /// 보상 대상 Player 행을 PostgreSQL의 배타적 행 잠금으로 확보합니다.
    /// </summary>
    /// <remarks>
    /// SELECT FOR UPDATE는 같은 Player 행을 잠그려는 다음 트랜잭션을 현재 트랜잭션 종료까지 기다리게 합니다.
    /// 이 메서드는 반드시 BeginTransactionAsync 이후, 지갑·인벤토리를 읽기 전에 호출해야 합니다.
    /// FromSqlInterpolated는 playerId를 SQL 문자열에 직접 붙이지 않고 DB 매개변수로 전달해 SQL Injection을 막습니다.
    /// </remarks>
    /// <param name="playerId">잠글 플레이어 식별자입니다.</param>
    /// <param name="cancellationToken">행 잠금을 기다리는 작업도 HTTP 요청 취소를 따르도록 전달합니다.</param>
    /// <returns>Player 행을 찾아 잠갔다면 true, 존재하지 않으면 false입니다.</returns>
    private async Task<bool> TryLockPlayerAsync(Guid playerId, CancellationToken cancellationToken)
    {
        // ToListAsync로 원본 SQL을 바로 실행합니다. player_id는 PK이므로 결과는 0개 또는 1개입니다.
        var lockedPlayers = await _gameDbContext.Players
            .FromSqlInterpolated(
                $"""
                SELECT player_id, nickname, created_at, updated_at
                FROM players
                WHERE player_id = {playerId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return lockedPlayers.Count == 1;
    }

    /// <summary>
    /// 재시도 요청이 최초 요청과 완전히 같은 보상인지 확인합니다.
    /// </summary>
    private static void EnsureSameRewardRequest(RewardAudit existingRewardAudit, RewardAudit requestedRewardAudit)
    {
        var isSameRequest = existingRewardAudit.PlayerId == requestedRewardAudit.PlayerId &&
                            existingRewardAudit.GoldAmount == requestedRewardAudit.GoldAmount &&
                            existingRewardAudit.ItemId == requestedRewardAudit.ItemId &&
                            existingRewardAudit.ItemQuantity == requestedRewardAudit.ItemQuantity &&
                            existingRewardAudit.Reason == requestedRewardAudit.Reason;

        if (!isSameRequest)
        {
            throw new IdempotencyKeyConflictException(requestedRewardAudit.RequestId);
        }
    }

    /// <summary>
    /// PostgreSQL이 request_id 고유 인덱스 중복으로 반환한 DB 오류인지 판별합니다.
    /// </summary>
    private static bool IsDuplicateRequestId(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RequestIdUniqueIndexName,
        };
    }
}
