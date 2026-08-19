using CoopGameServer.GrainContracts.Matchmaking;

namespace CoopGameServer.Grains.Matchmaking;

/// <summary>
/// MatchQueueGrain 한 개가 소유하는 대기 순서, 티켓 상태, 멱등성 요청 기록과 4인 조합 규칙입니다.
/// </summary>
/// <remarks>
/// 이 객체는 게임 규칙과 메모리 상태만 담당합니다.
/// PostgreSQL 읽기·쓰기와 트랜잭션은 MatchQueueGrain이 담당하므로,
/// DB 저장이 실패했을 때 실제 Grain 메모리를 이전 상태로 유지할 수 있습니다.
/// </remarks>
internal sealed class MatchQueueState
{
    /// <summary>이번 프로젝트의 협동 게임 방 정원입니다.</summary>
    internal const int TargetPlayerCount = 4;

    private readonly Dictionary<Guid, MatchQueueTicket> _ticketsById = [];
    private readonly List<Guid> _queuedTicketIds = [];
    private readonly Dictionary<Guid, MatchQueueStoredRequest> _requests = [];
    private long _nextQueueOrder;

    /// <summary>
    /// PostgreSQL에서 복원한 티켓과 요청 기록으로 메모리 규칙 객체를 만듭니다.
    /// </summary>
    internal static MatchQueueState Restore(
        IEnumerable<MatchQueueTicket> tickets,
        IEnumerable<MatchQueueStoredRequest> requests)
    {
        var state = new MatchQueueState();

        foreach (var ticket in tickets.OrderBy(ticket => ticket.QueueOrder))
        {
            state._ticketsById.Add(ticket.TicketId, CloneTicket(ticket));
            if (ticket.Status == MatchQueueTicketStatus.Queued)
            {
                state._queuedTicketIds.Add(ticket.TicketId);
            }
        }

        state._nextQueueOrder = state._ticketsById.Count == 0
            ? 0
            : state._ticketsById.Values.Max(ticket => ticket.QueueOrder);

        foreach (var request in requests)
        {
            state._requests.Add(request.RequestId, request.Copy());
        }

        return state;
    }

    /// <summary>
    /// DB 저장 실패 시 원본 메모리 상태를 보존할 수 있도록 명령 적용 전 깊은 복사본을 만듭니다.
    /// </summary>
    internal MatchQueueState Clone() => Restore(GetTickets(), GetStoredRequests());

    /// <summary>PostgreSQL 동기화에 사용할 모든 티켓의 방어적 복사본을 반환합니다.</summary>
    internal MatchQueueTicket[] GetTickets()
    {
        return _ticketsById.Values
            .OrderBy(ticket => ticket.QueueOrder)
            .Select(CloneTicket)
            .ToArray();
    }

    /// <summary>PostgreSQL 동기화에 사용할 멱등성 요청 기록의 복사본을 반환합니다.</summary>
    internal MatchQueueStoredRequest[] GetStoredRequests()
    {
        return _requests.Values
            .OrderBy(request => request.CreatedAt)
            .Select(request => request.Copy())
            .ToArray();
    }

    /// <summary>사전 구성 파티 또는 솔로 참가자를 하나의 대기 티켓으로 등록합니다.</summary>
    internal MatchQueueCommandResult Enqueue(string queueKey, MatchQueueEntryRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return Failure(ticketId: null, MatchQueueCommandError.InvalidRequestId);
        }

        if (_requests.TryGetValue(request.RequestId, out var storedRequest))
        {
            return storedRequest.Matches(request)
                ? Replay(storedRequest.Result)
                : Failure(ticketId: null, MatchQueueCommandError.RequestIdConflict);
        }

        var validationError = ValidateEnqueueRequest(request);
        if (validationError is not MatchQueueCommandError.None)
        {
            return Store(request, Failure(ticketId: null, validationError));
        }

        if (request.EntryKind == MatchQueueEntryKind.PreformedParty)
        {
            var existingPartyTicket = _ticketsById.Values.SingleOrDefault(ticket =>
                ticket.EntryKind == MatchQueueEntryKind.PreformedParty
                && ticket.PartyId == request.PartyId
                && ticket.Status is MatchQueueTicketStatus.Queued or MatchQueueTicketStatus.Matched);

            if (existingPartyTicket is not null)
            {
                var partyError = existingPartyTicket.Status == MatchQueueTicketStatus.Queued
                    ? MatchQueueCommandError.PartyAlreadyQueued
                    : MatchQueueCommandError.PartyAlreadyMatched;
                return Store(request, Failure(existingPartyTicket.TicketId, partyError));
            }
        }

        if (HasPlayerInAnotherCurrentTicket(request.MemberPlayerIds))
        {
            return Store(request, Failure(ticketId: null, MatchQueueCommandError.PlayerAlreadyQueued));
        }

        // 호출자가 전달한 배열을 복사하여 외부 변경이 Grain 내부 상태에 영향을 주지 못하게 합니다.
        var ticket = new MatchQueueTicket(
            Guid.NewGuid(),
            queueKey,
            request.EntryKind,
            request.PartyId,
            request.LeaderPlayerId,
            request.MemberPlayerIds.ToArray(),
            MatchQueueTicketStatus.Queued,
            RoomId: null,
            DateTimeOffset.UtcNow,
            QueueOrder: checked(++_nextQueueOrder));

        _ticketsById.Add(ticket.TicketId, ticket);
        _queuedTicketIds.Add(ticket.TicketId);

        var match = TryCreateMatch(queueKey);
        var resultTicket = CloneTicket(_ticketsById[ticket.TicketId]);
        return Store(request, Success(resultTicket, match));
    }

    /// <summary>티켓을 소유한 리더만 아직 대기 중인 매칭을 취소할 수 있도록 처리합니다.</summary>
    internal MatchQueueCommandResult Cancel(CancelMatchQueueRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return Failure(ticketId: null, MatchQueueCommandError.InvalidRequestId);
        }

        if (_requests.TryGetValue(request.RequestId, out var storedRequest))
        {
            return storedRequest.Matches(request)
                ? Replay(storedRequest.Result)
                : Failure(ticketId: request.TicketId, MatchQueueCommandError.RequestIdConflict);
        }

        if (request.TicketId == Guid.Empty)
        {
            return Store(request, Failure(ticketId: null, MatchQueueCommandError.TicketNotFound));
        }

        if (request.RequesterPlayerId == Guid.Empty)
        {
            return Store(request, Failure(request.TicketId, MatchQueueCommandError.InvalidLeaderPlayerId));
        }

        if (!_ticketsById.TryGetValue(request.TicketId, out var ticket))
        {
            return Store(request, Failure(ticketId: null, MatchQueueCommandError.TicketNotFound));
        }

        if (ticket.LeaderPlayerId != request.RequesterPlayerId)
        {
            return Store(request, Failure(ticket.TicketId, MatchQueueCommandError.OnlyLeaderCanCancel));
        }

        var statusError = ticket.Status switch
        {
            MatchQueueTicketStatus.Matched => MatchQueueCommandError.TicketAlreadyMatched,
            MatchQueueTicketStatus.Cancelled => MatchQueueCommandError.TicketAlreadyCancelled,
            MatchQueueTicketStatus.Completed => MatchQueueCommandError.TicketAlreadyCompleted,
            _ => MatchQueueCommandError.None,
        };

        if (statusError is not MatchQueueCommandError.None)
        {
            return Store(request, Failure(ticket.TicketId, statusError));
        }

        var cancelledTicket = ticket with
        {
            Status = MatchQueueTicketStatus.Cancelled,
            MemberPlayerIds = ticket.MemberPlayerIds.ToArray(),
        };

        _ticketsById[ticket.TicketId] = cancelledTicket;
        _queuedTicketIds.Remove(ticket.TicketId);

        return Store(request, Success(CloneTicket(cancelledTicket), match: null));
    }

    /// <summary>
    /// 완료된 roomId에 배정됐던 모든 티켓을 Completed로 바꿔 해당 참가자를 현재 매칭에서 해제합니다.
    /// </summary>
    internal MatchQueueCommandResult CompleteMatch(CompleteMatchQueueRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return Failure(ticketId: null, MatchQueueCommandError.InvalidRequestId);
        }

        if (_requests.TryGetValue(request.RequestId, out var storedRequest))
        {
            return storedRequest.Matches(request)
                ? Replay(storedRequest.Result)
                : Failure(ticketId: null, MatchQueueCommandError.RequestIdConflict);
        }

        if (request.RoomId == Guid.Empty)
        {
            return Store(request, Failure(ticketId: null, MatchQueueCommandError.InvalidRoomId));
        }

        var roomTickets = _ticketsById.Values
            .Where(ticket => ticket.RoomId == request.RoomId)
            .OrderBy(ticket => ticket.QueueOrder)
            .ToArray();

        if (roomTickets.Length == 0)
        {
            // GameRoomGrain 단독 테스트·복구처럼 Queue 티켓 없이 생성된 방이라면 해제할 점유도 없습니다.
            // 이 성공 결과도 저장하여 같은 요청의 재시도가 동일한 no-op 결과를 재생하게 합니다.
            return Store(request, SuccessWithoutTicket());
        }

        foreach (var ticket in roomTickets)
        {
            if (ticket.Status is not (MatchQueueTicketStatus.Matched or MatchQueueTicketStatus.Completed))
            {
                throw new InvalidOperationException(
                    $"게임 방 {request.RoomId}의 티켓 {ticket.TicketId} 상태가 완료 처리 가능한 상태가 아닙니다: {ticket.Status}");
            }

            _ticketsById[ticket.TicketId] = ticket with
            {
                Status = MatchQueueTicketStatus.Completed,
                MemberPlayerIds = ticket.MemberPlayerIds.ToArray(),
            };
        }

        var firstCompletedTicket = CloneTicket(_ticketsById[roomTickets[0].TicketId]);
        return Store(request, Success(firstCompletedTicket, match: null));
    }

    /// <summary>특정 티켓의 방어적 복사본을 반환합니다.</summary>
    internal MatchQueueTicket? GetTicket(Guid ticketId)
    {
        return _ticketsById.TryGetValue(ticketId, out var ticket)
            ? CloneTicket(ticket)
            : null;
    }

    /// <summary>현재 대기 중인 티켓만 등록 순서대로 복사하여 반환합니다.</summary>
    internal MatchQueueSnapshot GetSnapshot(string queueKey)
    {
        var queuedTickets = _queuedTicketIds
            .Select(ticketId => CloneTicket(_ticketsById[ticketId]))
            .ToArray();

        return new MatchQueueSnapshot(queueKey, TargetPlayerCount, queuedTickets);
    }

    private static MatchQueueCommandError ValidateEnqueueRequest(MatchQueueEntryRequest request)
    {
        if (request.LeaderPlayerId == Guid.Empty)
        {
            return MatchQueueCommandError.InvalidLeaderPlayerId;
        }

        if (request.MemberPlayerIds is null
            || request.MemberPlayerIds.Length is < 1 or > TargetPlayerCount
            || request.MemberPlayerIds.Any(playerId => playerId == Guid.Empty)
            || request.MemberPlayerIds.Distinct().Count() != request.MemberPlayerIds.Length)
        {
            return MatchQueueCommandError.InvalidMembers;
        }

        if (!request.MemberPlayerIds.Contains(request.LeaderPlayerId))
        {
            return MatchQueueCommandError.LeaderNotMember;
        }

        if (!Enum.IsDefined(request.EntryKind))
        {
            return MatchQueueCommandError.InvalidEntryShape;
        }

        if (request.EntryKind == MatchQueueEntryKind.PreformedParty
            && (!request.PartyId.HasValue || request.PartyId.Value == Guid.Empty))
        {
            return MatchQueueCommandError.InvalidEntryShape;
        }

        if (request.EntryKind == MatchQueueEntryKind.SoloPlayer
            && (request.PartyId.HasValue || request.MemberPlayerIds.Length != 1))
        {
            return MatchQueueCommandError.InvalidEntryShape;
        }

        return MatchQueueCommandError.None;
    }

    private bool HasPlayerInAnotherCurrentTicket(IEnumerable<Guid> memberPlayerIds)
    {
        var requestedPlayerIds = memberPlayerIds.ToHashSet();

        return _ticketsById.Values.Any(ticket =>
            ticket.Status is MatchQueueTicketStatus.Queued or MatchQueueTicketStatus.Matched
            && ticket.MemberPlayerIds.Any(requestedPlayerIds.Contains));
    }

    /// <summary>
    /// 가장 오래 기다린 티켓을 반드시 포함하면서 정확히 4명이 되는 가장 이른 조합을 찾습니다.
    /// </summary>
    private MatchAssignment? TryCreateMatch(string queueKey)
    {
        if (_queuedTicketIds.Count == 0)
        {
            return null;
        }

        var oldestTicketId = _queuedTicketIds[0];
        var oldestTicket = _ticketsById[oldestTicketId];
        var selectedTicketIds = new List<Guid> { oldestTicketId };
        var remainingPlayerCount = TargetPlayerCount - oldestTicket.MemberPlayerIds.Length;

        if (!TrySelectCombination(startIndex: 1, remainingPlayerCount, selectedTicketIds))
        {
            return null;
        }

        var roomId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var selectedTicketIdSet = selectedTicketIds.ToHashSet();
        var selectedTickets = selectedTicketIds.Select(ticketId => _ticketsById[ticketId]).ToArray();
        var partyIds = selectedTickets
            .Where(ticket => ticket.EntryKind == MatchQueueEntryKind.PreformedParty)
            .Select(ticket => ticket.PartyId!.Value)
            .ToArray();
        var playerIds = selectedTickets
            .SelectMany(ticket => ticket.MemberPlayerIds)
            .ToArray();

        foreach (var ticket in selectedTickets)
        {
            _ticketsById[ticket.TicketId] = ticket with
            {
                Status = MatchQueueTicketStatus.Matched,
                RoomId = roomId,
                MemberPlayerIds = ticket.MemberPlayerIds.ToArray(),
            };
        }

        _queuedTicketIds.RemoveAll(selectedTicketIdSet.Contains);

        return new MatchAssignment(
            roomId,
            queueKey,
            partyIds,
            playerIds,
            createdAt);
    }

    /// <summary>대기 순서대로 후보를 선택하는 깊이 우선 탐색으로 남은 인원을 정확히 채웁니다.</summary>
    private bool TrySelectCombination(
        int startIndex,
        int remainingPlayerCount,
        List<Guid> selectedTicketIds)
    {
        if (remainingPlayerCount == 0)
        {
            return true;
        }

        for (var index = startIndex; index < _queuedTicketIds.Count; index++)
        {
            var candidateTicketId = _queuedTicketIds[index];
            var candidatePlayerCount = _ticketsById[candidateTicketId].MemberPlayerIds.Length;
            if (candidatePlayerCount > remainingPlayerCount)
            {
                continue;
            }

            selectedTicketIds.Add(candidateTicketId);
            if (TrySelectCombination(index + 1, remainingPlayerCount - candidatePlayerCount, selectedTicketIds))
            {
                return true;
            }

            selectedTicketIds.RemoveAt(selectedTicketIds.Count - 1);
        }

        return false;
    }

    private MatchQueueCommandResult Store(
        MatchQueueEntryRequest request,
        MatchQueueCommandResult result)
    {
        _requests[request.RequestId] = MatchQueueStoredRequest.ForEnqueue(request, CloneResult(result));
        return result;
    }

    private MatchQueueCommandResult Store(
        CancelMatchQueueRequest request,
        MatchQueueCommandResult result)
    {
        _requests[request.RequestId] = MatchQueueStoredRequest.ForCancel(request, CloneResult(result));
        return result;
    }

    private MatchQueueCommandResult Store(
        CompleteMatchQueueRequest request,
        MatchQueueCommandResult result)
    {
        _requests[request.RequestId] = MatchQueueStoredRequest.ForCompleteMatch(request, CloneResult(result));
        return result;
    }

    private MatchQueueCommandResult Failure(Guid? ticketId, MatchQueueCommandError error)
    {
        return new MatchQueueCommandResult(
            IsReplay: false,
            Error: error,
            Ticket: ticketId is { } value ? GetTicket(value) : null,
            Match: null);
    }

    private static MatchQueueCommandResult Success(MatchQueueTicket ticket, MatchAssignment? match)
    {
        return new MatchQueueCommandResult(
            IsReplay: false,
            Error: MatchQueueCommandError.None,
            Ticket: ticket,
            Match: CloneMatch(match));
    }

    private static MatchQueueCommandResult SuccessWithoutTicket()
    {
        return new MatchQueueCommandResult(
            IsReplay: false,
            Error: MatchQueueCommandError.None,
            Ticket: null,
            Match: null);
    }

    private static MatchQueueCommandResult Replay(MatchQueueCommandResult result)
    {
        var copy = CloneResult(result);
        return copy with { IsReplay = true };
    }

    private static MatchQueueCommandResult CloneResult(MatchQueueCommandResult result)
    {
        return new MatchQueueCommandResult(
            result.IsReplay,
            result.Error,
            result.Ticket is null ? null : CloneTicket(result.Ticket),
            CloneMatch(result.Match));
    }

    private static MatchQueueTicket CloneTicket(MatchQueueTicket ticket)
    {
        return ticket with { MemberPlayerIds = ticket.MemberPlayerIds.ToArray() };
    }

    private static MatchAssignment? CloneMatch(MatchAssignment? match)
    {
        return match is null
            ? null
            : match with
            {
                PartyIds = match.PartyIds.ToArray(),
                PlayerIds = match.PlayerIds.ToArray(),
            };
    }
}

/// <summary>대기열 요청의 종류를 구분해 같은 requestId의 내용 충돌을 판별합니다.</summary>
internal enum MatchQueueCommandKind
{
    Enqueue = 0,
    Cancel = 1,
    CompleteMatch = 2,
}

/// <summary>
/// Silo 재시작 뒤에도 멱등성 재생을 유지하기 위해 저장하는 최초 요청과 결과입니다.
/// </summary>
internal sealed record MatchQueueStoredRequest(
    Guid RequestId,
    MatchQueueCommandKind CommandKind,
    MatchQueueEntryRequest? EnqueueRequest,
    CancelMatchQueueRequest? CancelRequest,
    CompleteMatchQueueRequest? CompleteMatchRequest,
    MatchQueueCommandResult Result,
    DateTimeOffset CreatedAt)
{
    internal static MatchQueueStoredRequest ForEnqueue(
        MatchQueueEntryRequest request,
        MatchQueueCommandResult result)
    {
        return new MatchQueueStoredRequest(
            request.RequestId,
            MatchQueueCommandKind.Enqueue,
            request with { MemberPlayerIds = request.MemberPlayerIds.ToArray() },
            CancelRequest: null,
            CompleteMatchRequest: null,
            result,
            DateTimeOffset.UtcNow);
    }

    internal static MatchQueueStoredRequest ForCancel(
        CancelMatchQueueRequest request,
        MatchQueueCommandResult result)
    {
        return new MatchQueueStoredRequest(
            request.RequestId,
            MatchQueueCommandKind.Cancel,
            EnqueueRequest: null,
            request,
            CompleteMatchRequest: null,
            result,
            DateTimeOffset.UtcNow);
    }

    internal static MatchQueueStoredRequest ForCompleteMatch(
        CompleteMatchQueueRequest request,
        MatchQueueCommandResult result)
    {
        return new MatchQueueStoredRequest(
            request.RequestId,
            MatchQueueCommandKind.CompleteMatch,
            EnqueueRequest: null,
            CancelRequest: null,
            request,
            result,
            DateTimeOffset.UtcNow);
    }

    internal bool Matches(MatchQueueEntryRequest request)
    {
        return CommandKind == MatchQueueCommandKind.Enqueue
            && EnqueueRequest is { } storedRequest
            && storedRequest.EntryKind == request.EntryKind
            && storedRequest.PartyId == request.PartyId
            && storedRequest.LeaderPlayerId == request.LeaderPlayerId
            && storedRequest.MemberPlayerIds.SequenceEqual(request.MemberPlayerIds ?? []);
    }

    internal bool Matches(CancelMatchQueueRequest request)
    {
        return CommandKind == MatchQueueCommandKind.Cancel
            && CancelRequest is { } storedRequest
            && storedRequest.TicketId == request.TicketId
            && storedRequest.RequesterPlayerId == request.RequesterPlayerId;
    }

    internal bool Matches(CompleteMatchQueueRequest request)
    {
        return CommandKind == MatchQueueCommandKind.CompleteMatch
            && CompleteMatchRequest is { } storedRequest
            && storedRequest.RoomId == request.RoomId;
    }

    internal MatchQueueStoredRequest Copy()
    {
        return this with
        {
            EnqueueRequest = EnqueueRequest is null
                ? null
                : EnqueueRequest with { MemberPlayerIds = EnqueueRequest.MemberPlayerIds.ToArray() },
            Result = CloneResult(Result),
        };
    }

    private static MatchQueueCommandResult CloneResult(MatchQueueCommandResult result)
    {
        return new MatchQueueCommandResult(
            result.IsReplay,
            result.Error,
            result.Ticket is null
                ? null
                : result.Ticket with { MemberPlayerIds = result.Ticket.MemberPlayerIds.ToArray() },
            result.Match is null
                ? null
                : result.Match with
                {
                    PartyIds = result.Match.PartyIds.ToArray(),
                    PlayerIds = result.Match.PlayerIds.ToArray(),
                });
    }
}
