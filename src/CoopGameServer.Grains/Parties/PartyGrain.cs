using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Parties;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.Grains.Parties;

/// <summary>
/// 파티 한 개의 명령을 Orleans 실행 순서 안에서 처리하고 PostgreSQL에 영속화하는 Grain 구현체입니다.
/// </summary>
/// <remarks>
/// PartyState는 게임 규칙만 판단하고, 이 클래스는 DB 복원·트랜잭션·멱등성 기록을 담당합니다.
/// 메모리 복사본에 먼저 명령을 적용한 뒤 DB 커밋이 성공했을 때만 실제 Grain 상태를 교체하므로,
/// DB 오류가 발생해도 메모리와 데이터베이스가 서로 다른 상태로 남지 않습니다.
/// </remarks>
public sealed class PartyGrain(IDbContextFactory<GameDbContext> dbContextFactory) : Grain, IPartyGrain
{
    private const string PartyMemberPlayerUniqueConstraint = "IX_party_members_player_id";
    private const string PartyRequestPrimaryKeyConstraint = "PK_party_requests";
    private const string PartyMemberPlayerForeignKeyConstraint = "FK_party_members_players_player_id";

    private PartyState _state = new();

    /// <summary>
    /// Grain이 처음 활성화될 때 PostgreSQL의 최신 파티 상태를 메모리로 복원합니다.
    /// </summary>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var partyId = GetPartyId();

        var party = await gameDbContext.Parties
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.PartyId == partyId, cancellationToken);

        if (party is not null)
        {
            var memberPlayerIds = await gameDbContext.PartyMembers
                .AsNoTracking()
                .Where(entity => entity.PartyId == partyId)
                .OrderBy(entity => entity.JoinOrder)
                .Select(entity => entity.PlayerId)
                .ToArrayAsync(cancellationToken);

            _state = PartyState.Restore(
                (PartyLifecycle)party.Lifecycle,
                party.LeaderPlayerId,
                memberPlayerIds);
        }

        await base.OnActivateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<PartyCommandResult> CreateAsync(Guid requestId, Guid leaderPlayerId)
    {
        return ExecuteCommandAsync(
            requestId,
            PartyCommandKind.Create,
            leaderPlayerId,
            state => state.Create(GetPartyId(), leaderPlayerId));
    }

    /// <inheritdoc />
    public Task<PartySnapshot?> GetAsync()
    {
        return Task.FromResult(_state.Get(GetPartyId()));
    }

    /// <inheritdoc />
    public Task<PartyCommandResult> JoinAsync(Guid requestId, Guid playerId)
    {
        return ExecuteCommandAsync(
            requestId,
            PartyCommandKind.Join,
            playerId,
            state => state.Join(GetPartyId(), playerId));
    }

    /// <inheritdoc />
    public Task<PartyCommandResult> LeaveAsync(Guid requestId, Guid playerId)
    {
        return ExecuteCommandAsync(
            requestId,
            PartyCommandKind.Leave,
            playerId,
            state => state.Leave(GetPartyId(), playerId));
    }

    /// <inheritdoc />
    public Task<PartyCommandResult> DisbandAsync(Guid requestId, Guid leaderPlayerId)
    {
        return ExecuteCommandAsync(
            requestId,
            PartyCommandKind.Disband,
            leaderPlayerId,
            state => state.Disband(GetPartyId(), leaderPlayerId));
    }

    /// <summary>
    /// 요청 중복 확인, 규칙 실행, 상태 저장, 최초 결과 기록을 하나의 DB 트랜잭션으로 처리합니다.
    /// </summary>
    private async Task<PartyCommandResult> ExecuteCommandAsync(
        Guid requestId,
        PartyCommandKind commandKind,
        Guid playerId,
        Func<PartyState, PartyCommandResult> executeCommand)
    {
        var partyId = GetPartyId();

        // 비어 있는 파티 ID와 요청 ID는 DB의 영속 키로 사용할 수 없으므로 기록하지 않습니다.
        if (partyId == Guid.Empty)
        {
            return _state.Failure(partyId, PartyCommandError.InvalidPartyId);
        }

        if (requestId == Guid.Empty)
        {
            return _state.Failure(partyId, PartyCommandError.InvalidRequestId);
        }

        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync();
        await using var transaction = await gameDbContext.Database.BeginTransactionAsync();

        try
        {
            var storedRequest = await gameDbContext.PartyRequests
                .AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.RequestId == requestId);

            if (storedRequest is not null)
            {
                await transaction.CommitAsync();
                return RestoreStoredResultOrConflict(storedRequest, partyId, commandKind, playerId);
            }

            // 후보 상태에 먼저 게임 규칙을 적용합니다. DB 저장이 실패하면 후보를 버리고 기존 _state를 유지합니다.
            var candidateState = _state.Clone();
            var result = executeCommand(candidateState);

            // 파티 자체 규칙이 성공했을 때만 players 테이블의 실재 여부를 확인합니다.
            // 예를 들어 이미 해산된 파티라면 낯선 플레이어 ID보다 PartyDisbanded가 먼저 반환되어야 합니다.
            if (result.Error == PartyCommandError.None
                && RequiresExistingPlayer(commandKind)
                && !await gameDbContext.Players.AsNoTracking().AnyAsync(entity => entity.Id == playerId))
            {
                candidateState = _state.Clone();
                result = candidateState.Failure(partyId, PartyCommandError.PlayerNotFound);
            }

            if (result.Error == PartyCommandError.None)
            {
                await SynchronizePartyStateAsync(gameDbContext, result.Party!);
            }

            gameDbContext.PartyRequests.Add(CreateRequestRecord(
                requestId,
                partyId,
                commandKind,
                playerId,
                result));

            await gameDbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            // DB 커밋 뒤에만 실제 Grain 메모리 상태를 교체합니다.
            _state = candidateState;
            return result;
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, PartyRequestPrimaryKeyConstraint))
        {
            // 서로 다른 Grain이 같은 requestId를 동시에 삽입했다면 한쪽만 성공합니다.
            // 실패한 쪽은 롤백한 뒤 승자의 최초 결과를 읽어 재생 또는 충돌로 판정합니다.
            await transaction.RollbackAsync();
            return await ReadStoredResultOrConflictAsync(requestId, partyId, commandKind, playerId);
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, PartyMemberPlayerUniqueConstraint))
        {
            // 서로 다른 PartyGrain은 Orleans 실행 큐를 공유하지 않으므로 DB UNIQUE 제약이 최종 동시성 방어선입니다.
            await transaction.RollbackAsync();
            var failure = _state.Failure(partyId, PartyCommandError.PlayerAlreadyInAnotherParty);
            return await StoreFailureOrReadRaceAsync(requestId, partyId, commandKind, playerId, failure);
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, PartyMemberPlayerForeignKeyConstraint))
        {
            // 존재 확인 직후 플레이어가 삭제되는 극히 짧은 경쟁 상황도 외래 키 위반을 업무 오류로 변환합니다.
            await transaction.RollbackAsync();
            var failure = _state.Failure(partyId, PartyCommandError.PlayerNotFound);
            return await StoreFailureOrReadRaceAsync(requestId, partyId, commandKind, playerId, failure);
        }
    }

    /// <summary>
    /// 성공한 후보 상태를 parties와 party_members 테이블에 동기화합니다.
    /// </summary>
    private static async Task SynchronizePartyStateAsync(
        GameDbContext gameDbContext,
        PartySnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var party = await gameDbContext.Parties
            .SingleOrDefaultAsync(entity => entity.PartyId == snapshot.PartyId);

        if (party is null)
        {
            party = new PartyRecord(
                snapshot.PartyId,
                (int)snapshot.Lifecycle,
                snapshot.LeaderPlayerId,
                now,
                now);
            gameDbContext.Parties.Add(party);
        }
        else
        {
            party.Update((int)snapshot.Lifecycle, snapshot.LeaderPlayerId, now);
        }

        var storedMembers = await gameDbContext.PartyMembers
            .Where(entity => entity.PartyId == snapshot.PartyId)
            .ToListAsync();
        var desiredJoinOrders = snapshot.MemberPlayerIds
            .Select((playerId, joinOrder) => new { playerId, joinOrder })
            .ToDictionary(item => item.playerId, item => item.joinOrder);

        foreach (var storedMember in storedMembers)
        {
            if (!desiredJoinOrders.TryGetValue(storedMember.PlayerId, out var joinOrder))
            {
                gameDbContext.PartyMembers.Remove(storedMember);
                continue;
            }

            storedMember.UpdateJoinOrder(joinOrder);
            desiredJoinOrders.Remove(storedMember.PlayerId);
        }

        foreach (var desiredMember in desiredJoinOrders)
        {
            gameDbContext.PartyMembers.Add(new PartyMemberRecord(
                snapshot.PartyId,
                desiredMember.Key,
                desiredMember.Value));
        }
    }

    /// <summary>
    /// DB 제약조건 때문에 상태 변경이 롤백된 경우에도 그 실패 결과 자체는 별도 요청 기록으로 보존합니다.
    /// </summary>
    private async Task<PartyCommandResult> StoreFailureOrReadRaceAsync(
        Guid requestId,
        Guid partyId,
        PartyCommandKind commandKind,
        Guid playerId,
        PartyCommandResult failure)
    {
        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync();

        var storedRequest = await gameDbContext.PartyRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.RequestId == requestId);

        if (storedRequest is not null)
        {
            return RestoreStoredResultOrConflict(storedRequest, partyId, commandKind, playerId);
        }

        gameDbContext.PartyRequests.Add(CreateRequestRecord(
            requestId,
            partyId,
            commandKind,
            playerId,
            failure));

        try
        {
            await gameDbContext.SaveChangesAsync();
            return failure;
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, PartyRequestPrimaryKeyConstraint))
        {
            return await ReadStoredResultOrConflictAsync(requestId, partyId, commandKind, playerId);
        }
    }

    /// <summary>
    /// 동시 삽입 경쟁에서 먼저 저장된 requestId의 결과를 새 DbContext로 다시 읽습니다.
    /// </summary>
    private async Task<PartyCommandResult> ReadStoredResultOrConflictAsync(
        Guid requestId,
        Guid partyId,
        PartyCommandKind commandKind,
        Guid playerId)
    {
        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync();
        var storedRequest = await gameDbContext.PartyRequests
            .AsNoTracking()
            .SingleAsync(entity => entity.RequestId == requestId);

        return RestoreStoredResultOrConflict(storedRequest, partyId, commandKind, playerId);
    }

    /// <summary>
    /// 같은 requestId와 같은 명령 본문이면 최초 결과를 재생하고, 본문이 다르면 충돌을 반환합니다.
    /// </summary>
    private PartyCommandResult RestoreStoredResultOrConflict(
        PartyRequestRecord storedRequest,
        Guid partyId,
        PartyCommandKind commandKind,
        Guid playerId)
    {
        if (storedRequest.PartyId != partyId
            || storedRequest.CommandKind != commandKind.ToString()
            || storedRequest.PlayerId != playerId)
        {
            return _state.Failure(partyId, PartyCommandError.RequestIdConflict);
        }

        PartySnapshot? storedSnapshot = null;
        if (storedRequest.ResultLifecycle is int resultLifecycle)
        {
            storedSnapshot = new PartySnapshot(
                storedRequest.PartyId,
                (PartyLifecycle)resultLifecycle,
                storedRequest.ResultLeaderPlayerId,
                storedRequest.ResultMemberPlayerIds?.ToArray() ?? []);
        }

        return new PartyCommandResult(
            IsReplay: true,
            Error: (PartyCommandError)storedRequest.ResultError,
            Party: storedSnapshot);
    }

    /// <summary>
    /// 최초 응답을 Silo 재시작 뒤에도 그대로 재구성할 수 있는 DB 행으로 변환합니다.
    /// </summary>
    private static PartyRequestRecord CreateRequestRecord(
        Guid requestId,
        Guid partyId,
        PartyCommandKind commandKind,
        Guid playerId,
        PartyCommandResult result)
    {
        return new PartyRequestRecord(
            requestId,
            partyId,
            commandKind.ToString(),
            playerId,
            (int)result.Error,
            result.Party is null ? null : (int)result.Party.Lifecycle,
            result.Party?.LeaderPlayerId,
            result.Party?.MemberPlayerIds.ToArray(),
            DateTimeOffset.UtcNow);
    }

    private static bool RequiresExistingPlayer(PartyCommandKind commandKind)
    {
        return commandKind is PartyCommandKind.Create or PartyCommandKind.Join;
    }

    private static bool HasConstraint(DbUpdateException exception, string constraintName)
    {
        return exception.InnerException is PostgresException postgresException
            && postgresException.ConstraintName == constraintName;
    }

    /// <summary>
    /// Orleans가 Grain 참조에 부여한 Guid 기본 키를 partyId로 읽습니다.
    /// </summary>
    private Guid GetPartyId() => this.GetPrimaryKey();

    private enum PartyCommandKind
    {
        Create,
        Join,
        Leave,
        Disband,
    }
}
