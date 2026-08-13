using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Parties;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Api.Application.Parties;

/// <summary>
/// HTTP 파티 요청을 PartyGrain 호출로 조정하는 애플리케이션 서비스입니다.
/// </summary>
/// <remarks>
/// 일반 파티 명령은 partyId로 Grain을 바로 찾을 수 있지만, 생성 요청은 서버가 새 partyId를 만듭니다.
/// 같은 생성 요청이 재전송됐을 때 새 partyId를 다시 만들면 멱등성 키가 충돌하므로,
/// party_requests에 저장된 최초 partyId를 먼저 찾아 같은 Grain의 결과를 재생합니다.
/// </remarks>
public sealed class PartyService(IGrainFactory grainFactory, GameDbContext gameDbContext)
{
    private const string CreateCommandKind = "Create";

    /// <summary>
    /// 서버가 새 partyId를 생성하고, 같은 requestId의 재시도에는 최초 partyId를 재사용합니다.
    /// </summary>
    public async Task<PartyCommandResult> CreateAsync(
        Guid requestId,
        Guid leaderPlayerId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return Failure(PartyCommandError.InvalidRequestId);
        }

        if (leaderPlayerId == Guid.Empty)
        {
            return Failure(PartyCommandError.InvalidPlayerId);
        }

        var storedRequest = await FindRequestAsync(requestId, cancellationToken);
        if (storedRequest is not null)
        {
            return await ReplayCreateOrReturnConflictAsync(
                storedRequest,
                requestId,
                leaderPlayerId,
                cancellationToken);
        }

        var partyId = Guid.NewGuid();
        var result = await GetParty(partyId)
            .CreateAsync(requestId, leaderPlayerId)
            .WaitAsync(cancellationToken);

        if (result.Error is not PartyCommandError.RequestIdConflict)
        {
            return result;
        }

        // 동일 requestId의 생성 요청 두 개가 동시에 서로 다른 partyId를 만들었을 수 있습니다.
        // DB에서 먼저 저장된 승자의 partyId를 다시 읽고 그 Grain의 최초 결과를 반환합니다.
        gameDbContext.ChangeTracker.Clear();
        storedRequest = await FindRequestAsync(requestId, cancellationToken);

        return storedRequest is null
            ? result
            : await ReplayCreateOrReturnConflictAsync(
                storedRequest,
                requestId,
                leaderPlayerId,
                cancellationToken);
    }

    /// <summary>파티의 현재 상태를 조회합니다.</summary>
    public Task<PartySnapshot?> GetAsync(Guid partyId, CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty)
        {
            return Task.FromResult<PartySnapshot?>(null);
        }

        return GetParty(partyId).GetAsync().WaitAsync(cancellationToken);
    }

    /// <summary>플레이어를 파티에 가입시킵니다.</summary>
    public Task<PartyCommandResult> JoinAsync(
        Guid partyId,
        Guid requestId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty)
        {
            return Task.FromResult(Failure(PartyCommandError.InvalidPartyId));
        }

        return GetParty(partyId)
            .JoinAsync(requestId, playerId)
            .WaitAsync(cancellationToken);
    }

    /// <summary>플레이어를 파티에서 탈퇴시킵니다.</summary>
    public Task<PartyCommandResult> LeaveAsync(
        Guid partyId,
        Guid requestId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty)
        {
            return Task.FromResult(Failure(PartyCommandError.InvalidPartyId));
        }

        return GetParty(partyId)
            .LeaveAsync(requestId, playerId)
            .WaitAsync(cancellationToken);
    }

    /// <summary>현재 리더의 요청으로 파티를 해산합니다.</summary>
    public Task<PartyCommandResult> DisbandAsync(
        Guid partyId,
        Guid requestId,
        Guid leaderPlayerId,
        CancellationToken cancellationToken)
    {
        if (partyId == Guid.Empty)
        {
            return Task.FromResult(Failure(PartyCommandError.InvalidPartyId));
        }

        return GetParty(partyId)
            .DisbandAsync(requestId, leaderPlayerId)
            .WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 저장된 생성 명령의 내용이 현재 요청과 같으면 최초 partyId의 Grain을 호출하고,
    /// 다르면 같은 멱등성 키를 다른 내용에 재사용한 충돌을 반환합니다.
    /// </summary>
    private async Task<PartyCommandResult> ReplayCreateOrReturnConflictAsync(
        PartyRequestRecord storedRequest,
        Guid requestId,
        Guid leaderPlayerId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(storedRequest.CommandKind, CreateCommandKind, StringComparison.Ordinal)
            || storedRequest.PlayerId != leaderPlayerId)
        {
            return Failure(PartyCommandError.RequestIdConflict);
        }

        return await GetParty(storedRequest.PartyId)
            .CreateAsync(requestId, leaderPlayerId)
            .WaitAsync(cancellationToken);
    }

    /// <summary>추적되지 않는 읽기 전용 질의로 최초 요청 기록을 찾습니다.</summary>
    private Task<PartyRequestRecord?> FindRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return gameDbContext.PartyRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(request => request.RequestId == requestId, cancellationToken);
    }

    /// <summary>객체를 직접 생성하지 않고 Orleans가 관리하는 PartyGrain 참조를 얻습니다.</summary>
    private IPartyGrain GetParty(Guid partyId)
    {
        return grainFactory.GetGrain<IPartyGrain>(partyId);
    }

    private static PartyCommandResult Failure(PartyCommandError error)
    {
        return new PartyCommandResult(IsReplay: false, Error: error, Party: null);
    }
}
