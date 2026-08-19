using System.Security.Cryptography;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Api.Application.Matchmaking;

/// <summary>
/// 인증된 HTTP 요청을 PartyGrain과 MatchQueueGrain 호출 순서로 조정하는 애플리케이션 서비스입니다.
/// </summary>
/// <remarks>
/// 외부 클라이언트가 파티 멤버 배열이나 리더 ID를 직접 보내게 두지 않습니다.
/// 사전 구성 파티는 PartyGrain의 최신 스냅샷을, 솔로 신청은 인증 토큰의 Player ID를 사용해
/// 신뢰할 수 있는 내부 MatchQueueEntryRequest를 만듭니다.
/// </remarks>
public sealed class MatchmakingService(IGrainFactory grainFactory, GameDbContext gameDbContext)
{
    /// <summary>PostgreSQL queue_key 열과 같은 최대 길이입니다.</summary>
    public const int MaxQueueKeyLength = 100;

    /// <summary>인증된 플레이어 한 명을 파티 없는 솔로 티켓으로 등록합니다.</summary>
    public async Task<MatchmakingApplicationResult> EnqueueSoloAsync(
        string queueKey,
        Guid requestId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        if (!IsValidQueueKey(queueKey))
        {
            return Failure(MatchmakingApplicationError.InvalidQueueKey);
        }

        if (requestId == Guid.Empty)
        {
            return QueueFailure(MatchQueueCommandError.InvalidRequestId);
        }

        if (playerId == Guid.Empty)
        {
            return QueueFailure(MatchQueueCommandError.InvalidLeaderPlayerId);
        }

        // 사전 구성 파티에 남아 있는 플레이어가 동시에 솔로로 신청하면
        // 게임 종료 뒤 돌아갈 로비 상태가 모호해지므로 HTTP 경계에서 차단합니다.
        var belongsToParty = await gameDbContext.PartyMembers
            .AsNoTracking()
            .AnyAsync(member => member.PlayerId == playerId, cancellationToken);
        if (belongsToParty)
        {
            return Failure(MatchmakingApplicationError.SoloPlayerAlreadyInParty);
        }

        var request = new MatchQueueEntryRequest(
            requestId,
            MatchQueueEntryKind.SoloPlayer,
            PartyId: null,
            playerId,
            [playerId]);
        var queueResult = await GetQueue(queueKey)
            .EnqueueAsync(request)
            .WaitAsync(cancellationToken);

        return Success(queueResult);
    }

    /// <summary>
    /// 리더 권한과 실제 멤버 구성을 PartyGrain에서 확인한 뒤 파티 전체를 한 티켓으로 등록합니다.
    /// </summary>
    public async Task<MatchmakingApplicationResult> EnqueuePartyAsync(
        string queueKey,
        Guid partyId,
        Guid requestId,
        Guid requesterPlayerId,
        bool isAdministrator,
        CancellationToken cancellationToken)
    {
        if (!IsValidQueueKey(queueKey))
        {
            return Failure(MatchmakingApplicationError.InvalidQueueKey);
        }

        if (partyId == Guid.Empty)
        {
            return QueueFailure(MatchQueueCommandError.InvalidPartyId);
        }

        if (requestId == Guid.Empty)
        {
            return QueueFailure(MatchQueueCommandError.InvalidRequestId);
        }

        var party = grainFactory.GetGrain<IPartyGrain>(partyId);
        var snapshot = await party.GetAsync().WaitAsync(cancellationToken);
        if (snapshot is null)
        {
            return Failure(MatchmakingApplicationError.PartyNotFound);
        }

        var leaderPlayerId = snapshot.LeaderPlayerId;
        if (leaderPlayerId is null)
        {
            return Failure(
                MatchmakingApplicationError.PartyTransitionFailed,
                PartyCommandError.PartyDisbanded);
        }

        if (!isAdministrator && leaderPlayerId.Value != requesterPlayerId)
        {
            return Failure(MatchmakingApplicationError.RequesterIsNotPartyLeader);
        }

        // 하나의 외부 요청이 PartyGrain과 MatchQueueGrain 양쪽을 호출합니다.
        // 파티 명령에는 결정적인 하위 requestId를 써서 같은 HTTP 요청 재전송이 같은 결과로 수렴하게 합니다.
        var partyQueueRequestId = CreateChildRequestId(requestId, partyId, operationMarker: 1);
        var partyResult = await party
            .QueueForMatchAsync(partyQueueRequestId, leaderPlayerId.Value)
            .WaitAsync(cancellationToken);
        if (partyResult.Error is not PartyCommandError.None)
        {
            return Failure(MatchmakingApplicationError.PartyTransitionFailed, partyResult.Error);
        }

        var queuedParty = partyResult.Party
            ?? throw new InvalidOperationException("매칭 대기 전환에 성공한 파티 스냅샷이 없습니다.");
        var queueRequest = new MatchQueueEntryRequest(
            requestId,
            MatchQueueEntryKind.PreformedParty,
            partyId,
            leaderPlayerId.Value,
            queuedParty.MemberPlayerIds.ToArray());
        var queueResult = await GetQueue(queueKey)
            .EnqueueAsync(queueRequest)
            .WaitAsync(cancellationToken);

        if (queueResult.Error is not MatchQueueCommandError.None)
        {
            // 대기열이 명시적으로 거부했다면 멤버 잠금이 남지 않도록 Active 상태로 보상 복구합니다.
            // Orleans 호출 예외처럼 성공 여부를 모르는 경우에는 여기까지 오지 않으므로 섣불리 되돌리지 않습니다.
            var compensationRequestId = CreateChildRequestId(requestId, partyId, operationMarker: 2);
            var compensation = await party
                .CancelMatchQueueAsync(compensationRequestId, leaderPlayerId.Value)
                .WaitAsync(cancellationToken);
            if (compensation.Error is not PartyCommandError.None)
            {
                return Failure(MatchmakingApplicationError.PartyCompensationFailed, compensation.Error);
            }
        }

        return Success(queueResult);
    }

    /// <summary>티켓 소유자 또는 관리자의 요청만 대기를 취소하고 사전 구성 파티의 잠금을 풉니다.</summary>
    public async Task<MatchmakingApplicationResult> CancelAsync(
        string queueKey,
        Guid ticketId,
        Guid requestId,
        Guid requesterPlayerId,
        bool isAdministrator,
        CancellationToken cancellationToken)
    {
        if (!IsValidQueueKey(queueKey))
        {
            return Failure(MatchmakingApplicationError.InvalidQueueKey);
        }

        if (requestId == Guid.Empty)
        {
            return QueueFailure(MatchQueueCommandError.InvalidRequestId);
        }

        var queue = GetQueue(queueKey);
        var ticket = await queue.GetTicketAsync(ticketId).WaitAsync(cancellationToken);
        if (ticket is null)
        {
            return QueueFailure(MatchQueueCommandError.TicketNotFound);
        }

        if (!isAdministrator && ticket.LeaderPlayerId != requesterPlayerId)
        {
            return Failure(MatchmakingApplicationError.RequesterCannotManageTicket);
        }

        var queueResult = await queue
            .CancelAsync(new CancelMatchQueueRequest(
                requestId,
                ticketId,
                ticket.LeaderPlayerId))
            .WaitAsync(cancellationToken);

        if (queueResult.Error is MatchQueueCommandError.None && ticket.PartyId is Guid partyId)
        {
            var partyRequestId = CreateChildRequestId(requestId, partyId, operationMarker: 3);
            var partyResult = await grainFactory.GetGrain<IPartyGrain>(partyId)
                .CancelMatchQueueAsync(partyRequestId, ticket.LeaderPlayerId)
                .WaitAsync(cancellationToken);
            if (partyResult.Error is not PartyCommandError.None)
            {
                return Failure(MatchmakingApplicationError.PartyTransitionFailed, partyResult.Error);
            }
        }

        return Success(queueResult);
    }

    /// <summary>인증된 호출자가 열람 권한을 검사할 수 있도록 티켓 원본 스냅샷을 반환합니다.</summary>
    public Task<MatchQueueTicket?> GetTicketAsync(
        string queueKey,
        Guid ticketId,
        CancellationToken cancellationToken)
    {
        if (!IsValidQueueKey(queueKey) || ticketId == Guid.Empty)
        {
            return Task.FromResult<MatchQueueTicket?>(null);
        }

        return GetQueue(queueKey).GetTicketAsync(ticketId).WaitAsync(cancellationToken);
    }

    /// <summary>라우트 문자열이 DB 열 범위 안의 명시적인 대기열 키인지 확인합니다.</summary>
    private static bool IsValidQueueKey(string queueKey)
    {
        return !string.IsNullOrWhiteSpace(queueKey)
            && queueKey.Length <= MaxQueueKeyLength
            && string.Equals(queueKey, queueKey.Trim(), StringComparison.Ordinal);
    }

    /// <summary>문자열 기본 키로 Orleans가 관리하는 대기열 Grain 참조를 얻습니다.</summary>
    private IMatchQueueGrain GetQueue(string queueKey)
    {
        return grainFactory.GetGrain<IMatchQueueGrain>(queueKey);
    }

    /// <summary>외부 requestId·파티·작업 종류로 항상 같은 하위 요청 식별자를 만듭니다.</summary>
    private static Guid CreateChildRequestId(Guid requestId, Guid partyId, byte operationMarker)
    {
        Span<byte> source = stackalloc byte[33];
        requestId.TryWriteBytes(source[..16]);
        partyId.TryWriteBytes(source.Slice(16, 16));
        source[32] = operationMarker;

        var hash = SHA256.HashData(source);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static MatchmakingApplicationResult Success(MatchQueueCommandResult queueResult)
    {
        return new MatchmakingApplicationResult(
            MatchmakingApplicationError.None,
            PartyError: null,
            queueResult);
    }

    private static MatchmakingApplicationResult Failure(
        MatchmakingApplicationError error,
        PartyCommandError? partyError = null)
    {
        return new MatchmakingApplicationResult(error, partyError, QueueResult: null);
    }

    private static MatchmakingApplicationResult QueueFailure(MatchQueueCommandError error)
    {
        return Success(new MatchQueueCommandResult(
            IsReplay: false,
            Error: error,
            Ticket: null,
            Match: null));
    }
}

/// <summary>여러 Grain을 조정하는 과정에서 발생한 애플리케이션 계층 오류입니다.</summary>
public enum MatchmakingApplicationError
{
    None = 0,
    InvalidQueueKey = 1,
    PartyNotFound = 2,
    RequesterIsNotPartyLeader = 3,
    SoloPlayerAlreadyInParty = 4,
    RequesterCannotManageTicket = 5,
    PartyTransitionFailed = 6,
    PartyCompensationFailed = 7,
}

/// <summary>애플리케이션 조정 오류와 MatchQueueGrain의 업무 결과를 함께 전달합니다.</summary>
public sealed record MatchmakingApplicationResult(
    MatchmakingApplicationError Error,
    PartyCommandError? PartyError,
    MatchQueueCommandResult? QueueResult);
