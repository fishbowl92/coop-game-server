using CoopGameServer.Domain.Inventories;
using CoopGameServer.Domain.Rewards;
using CoopGameServer.Domain.Wallets;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.Persistence.Rewards;

/// <summary>
/// 보상 감사 이력, 지갑, 인벤토리를 하나의 PostgreSQL Transaction으로 변경합니다.
/// </summary>
/// <remarks>
/// 요청마다 <see cref="IDbContextFactory{TContext}"/>에서 새 <see cref="GameDbContext"/>를 만들므로
/// Singleton(싱글턴, 프로세스에 하나만 두는 객체)으로 등록해도 DbContext를 여러 호출이 공유하지 않습니다.
/// 같은 Player 행을 <c>SELECT FOR UPDATE</c>로 잠근 뒤 읽기-수정-쓰기를 수행해
/// 서로 다른 동시 보상의 골드와 아이템 수량이 유실되지 않도록 합니다.
/// </remarks>
public sealed class PostgreSqlRewardWriter : IRewardWriter
{
    private const string RequestIdUniqueIndexName = "IX_reward_audits_request_id";

    private readonly IDbContextFactory<GameDbContext> _gameDbContextFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>호출별 DbContext Factory와 서버 시각 공급자를 보관합니다.</summary>
    /// <param name="gameDbContextFactory">각 보상 작업에 독립적인 DbContext를 만드는 Factory입니다.</param>
    /// <param name="timeProvider">테스트 가능한 UTC 서버 시각을 제공하는 객체입니다.</param>
    public PostgreSqlRewardWriter(
        IDbContextFactory<GameDbContext> gameDbContextFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(gameDbContextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _gameDbContextFactory = gameDbContextFactory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<RewardWriteResult> WriteAsync(RewardWriteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 도메인 객체 생성이 입력 형태를 검증하고 지급 사유를 Trim하여 정규화합니다.
        // 하나의 now 값을 감사 이력, 지갑, 인벤토리 변경에 함께 사용해 시각 기준을 통일합니다.
        var requestedRewardAudit = new RewardAudit(
            Guid.NewGuid(),
            command.RequestId,
            command.PlayerId,
            command.GoldAmount,
            command.ItemId,
            command.ItemQuantity,
            command.Reason,
            _timeProvider.GetUtcNow());

        // 외부 HTTP 취소 토큰을 전달하지 않습니다. 시작된 멱등성 작업은 서버에서 끝까지 확정합니다.
        await using var gameDbContext = await _gameDbContextFactory.CreateDbContextAsync(CancellationToken.None);

        var existingRewardAudit = await FindRewardAuditAsync(
            gameDbContext,
            requestedRewardAudit.RequestId);

        if (existingRewardAudit is not null)
        {
            return IsSameRewardRequest(existingRewardAudit, requestedRewardAudit)
                ? ToSuccessResult(existingRewardAudit, isReplay: true)
                : ToErrorResult(RewardWriteError.IdempotencyConflict);
        }

        await using var transaction = await gameDbContext.Database.BeginTransactionAsync(CancellationToken.None);

        try
        {
            // 지갑·인벤토리 행이 아직 없어도 반드시 존재하는 Player 행을 잠금 기준으로 사용합니다.
            var playerExists = await TryLockPlayerAsync(gameDbContext, command.PlayerId);

            if (!playerExists)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return ToErrorResult(RewardWriteError.PlayerNotFound);
            }

            // request_id UNIQUE 인덱스가 같은 요청의 동시 처리 중 한 작업만 통과시킵니다.
            gameDbContext.RewardAudits.Add(requestedRewardAudit);
            await gameDbContext.SaveChangesAsync(CancellationToken.None);

            // 아이템 전용 보상도 지갑 행을 생성해 이후 진행도 조회 구조를 일정하게 유지합니다.
            var wallet = await gameDbContext.PlayerWallets.SingleOrDefaultAsync(
                entity => entity.PlayerId == command.PlayerId,
                CancellationToken.None);

            if (wallet is null)
            {
                wallet = new PlayerWallet(command.PlayerId, requestedRewardAudit.CreatedAt);
                gameDbContext.PlayerWallets.Add(wallet);
            }

            if (requestedRewardAudit.GoldAmount > 0)
            {
                wallet.AddGold(requestedRewardAudit.GoldAmount, requestedRewardAudit.CreatedAt);
            }

            if (requestedRewardAudit.ItemId is int itemId &&
                requestedRewardAudit.ItemQuantity is int itemQuantity)
            {
                var inventoryItem = await gameDbContext.InventoryItems.SingleOrDefaultAsync(
                    entity => entity.PlayerId == command.PlayerId && entity.ItemId == itemId,
                    CancellationToken.None);

                if (inventoryItem is null)
                {
                    inventoryItem = new InventoryItem(
                        command.PlayerId,
                        itemId,
                        itemQuantity,
                        requestedRewardAudit.CreatedAt);
                    gameDbContext.InventoryItems.Add(inventoryItem);
                }
                else
                {
                    inventoryItem.AddQuantity(itemQuantity, requestedRewardAudit.CreatedAt);
                }
            }

            // 세 테이블 변경이 모두 성공한 경우에만 보상 작업 전체를 Commit합니다.
            await gameDbContext.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);

            return ToSuccessResult(requestedRewardAudit, isReplay: false);
        }
        catch (DbUpdateException exception) when (IsDuplicateRequestId(exception))
        {
            // 경쟁 요청이 같은 키를 먼저 Commit했으면 이번 변경을 버리고 승자의 결과를 다시 읽습니다.
            await transaction.RollbackAsync(CancellationToken.None);
            gameDbContext.ChangeTracker.Clear();

            var competingRewardAudit = await FindRewardAuditAsync(
                gameDbContext,
                requestedRewardAudit.RequestId);

            // UNIQUE 위반 뒤 승자 행이 없으면 예상하지 못한 DB 상태이므로 원래 예외를 보존합니다.
            if (competingRewardAudit is null)
            {
                throw;
            }

            return IsSameRewardRequest(competingRewardAudit, requestedRewardAudit)
                ? ToSuccessResult(competingRewardAudit, isReplay: true)
                : ToErrorResult(RewardWriteError.IdempotencyConflict);
        }
    }

    /// <summary>멱등성 키로 기존 보상 감사 이력을 추적 없이 조회합니다.</summary>
    private static async Task<RewardAudit?> FindRewardAuditAsync(
        GameDbContext gameDbContext,
        Guid requestId)
    {
        return await gameDbContext.RewardAudits
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.RequestId == requestId,
                CancellationToken.None);
    }

    /// <summary>해당 Player 행을 트랜잭션 종료 시점까지 배타적으로 잠급니다.</summary>
    private static async Task<bool> TryLockPlayerAsync(
        GameDbContext gameDbContext,
        Guid playerId)
    {
        // playerId는 문자열 결합이 아니라 DB 매개변수로 전달되어 SQL Injection을 막습니다.
        var lockedPlayers = await gameDbContext.Players
            .FromSqlInterpolated(
                $"""
                SELECT player_id, nickname, created_at, updated_at
                FROM players
                WHERE player_id = {playerId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .ToListAsync(CancellationToken.None);

        return lockedPlayers.Count == 1;
    }

    /// <summary>기존 요청과 새 요청의 지급 대상·수량·사유가 완전히 같은지 확인합니다.</summary>
    private static bool IsSameRewardRequest(
        RewardAudit existingRewardAudit,
        RewardAudit requestedRewardAudit)
    {
        return existingRewardAudit.PlayerId == requestedRewardAudit.PlayerId &&
               existingRewardAudit.GoldAmount == requestedRewardAudit.GoldAmount &&
               existingRewardAudit.ItemId == requestedRewardAudit.ItemId &&
               existingRewardAudit.ItemQuantity == requestedRewardAudit.ItemQuantity &&
               existingRewardAudit.Reason == requestedRewardAudit.Reason;
    }

    /// <summary>PostgreSQL request_id 고유 인덱스의 중복 오류인지 확인합니다.</summary>
    private static bool IsDuplicateRequestId(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RequestIdUniqueIndexName,
        };
    }

    /// <summary>도메인 감사 이력을 외부에 안전하게 전달할 읽기 전용 영수증으로 바꿉니다.</summary>
    private static RewardWriteResult ToSuccessResult(RewardAudit rewardAudit, bool isReplay)
    {
        var receipt = new RewardWriteReceipt(
            rewardAudit.Id,
            rewardAudit.RequestId,
            rewardAudit.PlayerId,
            rewardAudit.GoldAmount,
            rewardAudit.ItemId,
            rewardAudit.ItemQuantity,
            rewardAudit.Reason,
            rewardAudit.CreatedAt);

        return isReplay
            ? RewardWriteResult.Replayed(receipt)
            : RewardWriteResult.Applied(receipt);
    }

    /// <summary>DB를 변경하지 않은 예상 업무 오류 결과를 만듭니다.</summary>
    private static RewardWriteResult ToErrorResult(RewardWriteError error)
    {
        return RewardWriteResult.Failed(error);
    }
}
