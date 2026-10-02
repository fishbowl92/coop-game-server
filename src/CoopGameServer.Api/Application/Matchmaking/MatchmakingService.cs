using CoopGameServer.Contracts.Matchmaking;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.Api.Application.Matchmaking;

/// <summary>
/// 인증된 HTTP 요청을 PartyGrain과 MatchQueueGrain 호출 순서로 조정하는 애플리케이션 서비스입니다.
/// </summary>
/// <remarks>
/// 외부 클라이언트가 파티 멤버 배열이나 리더 ID를 직접 보내게 두지 않습니다.
/// 사전 구성 파티는 PartyGrain의 최신 스냅샷을, 솔로 신청은 인증 토큰의 Player ID를 사용해
/// 신뢰할 수 있는 내부 MatchQueueEntryRequest를 만듭니다.
/// </remarks>
public sealed class MatchmakingService(IGrainFactory grainFactory)
{
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

        // 파티 소속·솔로 참가 경쟁은 Queue의 DB 잠금 안에서 판정합니다.
        // HTTP 사전 조회로 오래된 성공 요청의 재생을 차단하지 않습니다.
        cancellationToken.ThrowIfCancellationRequested();
        var request = new MatchQueueEntryRequest(
            requestId,
            MatchQueueEntryKind.SoloPlayer,
            PartyId: null,
            playerId,
            [playerId]);
        var queueResult = await GetQueue(queueKey)
            .EnqueueAsync(request)
            .WaitAsync(cancellationToken);

        return queueResult.Error == MatchQueueCommandError.SoloPlayerAlreadyInParty
            ? Failure(MatchmakingApplicationError.SoloPlayerAlreadyInParty)
            : Success(queueResult);
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

        return await ExecuteOperationAsync(new MatchmakingOperationRequest(queueKey, requestId,
            MatchmakingOperationKind.EnqueueParty, partyId, requesterPlayerId, isAdministrator), cancellationToken);
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

        if (ticketId == Guid.Empty) return QueueFailure(MatchQueueCommandError.TicketNotFound);
        return await ExecuteOperationAsync(new MatchmakingOperationRequest(queueKey, requestId,
            MatchmakingOperationKind.Cancel, ticketId, requesterPlayerId, isAdministrator), cancellationToken);
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

    /// <summary>
    /// 라우트 문자열이 클라이언트가 임의 생성한 Grain 키가 아니라 현재 서버가 지원하는 Queue인지 확인합니다.
    /// </summary>
    private static bool IsValidQueueKey(string queueKey)
    {
        return string.Equals(
            queueKey,
            MatchmakingQueueKeys.CoopDungeonNormalV1,
            StringComparison.Ordinal);
    }

    /// <summary>문자열 기본 키로 Orleans가 관리하는 대기열 Grain 참조를 얻습니다.</summary>
    private IMatchQueueGrain GetQueue(string queueKey)
    {
        return grainFactory.GetGrain<IMatchQueueGrain>(queueKey);
    }

    /// <summary>응답 대기만 취소합니다. 영속 의도를 수락한 서버 Grain은 후속 단계를 계속 실행합니다.</summary>
    private async Task<MatchmakingApplicationResult> ExecuteOperationAsync(
        MatchmakingOperationRequest request, CancellationToken cancellationToken)
    {
        var result = await grainFactory.GetGrain<IMatchmakingOperationGrain>(request.GetGrainKey())
            .ExecuteAsync(request).WaitAsync(cancellationToken);
        var error = result.Error switch
        {
            MatchmakingOperationError.None => MatchmakingApplicationError.None,
            MatchmakingOperationError.PartyNotFound => MatchmakingApplicationError.PartyNotFound,
            MatchmakingOperationError.RequesterIsNotPartyLeader => MatchmakingApplicationError.RequesterIsNotPartyLeader,
            MatchmakingOperationError.RequesterCannotManageTicket => MatchmakingApplicationError.RequesterCannotManageTicket,
            MatchmakingOperationError.PartyTransitionFailed => MatchmakingApplicationError.PartyTransitionFailed,
            MatchmakingOperationError.PartyCompensationFailed => MatchmakingApplicationError.PartyCompensationFailed,
            _ => throw new InvalidOperationException("지원하지 않는 매칭 작업 결과입니다."),
        };
        return new MatchmakingApplicationResult(error, result.PartyError, result.QueueResult);
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
