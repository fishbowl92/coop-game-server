using CoopGameServer.GrainContracts.Matchmaking;

namespace CoopGameServer.Grains.Matchmaking;

/// <summary>
/// MatchQueueGrain 한 개가 소유하는 대기 순서, 티켓 상태, 멱등성 요청 기록과 4인 조합 규칙입니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 메모리에만 존재하므로 Silo가 재시작되면 사라집니다.
/// 규칙을 충분히 검증한 다음 작업에서 PostgreSQL 영속 저장을 연결합니다.
/// </remarks>
internal sealed class MatchQueueState
{
    /// <summary>이번 프로젝트의 협동 게임 방 정원입니다.</summary>
    internal const int TargetPlayerCount = 4;

    private readonly Dictionary<Guid, MatchQueueTicket> _ticketsByPartyId = [];
    private readonly List<Guid> _queuedPartyIds = [];
    private readonly Dictionary<Guid, StoredRequest> _requests = [];

    /// <summary>파티 전체를 분할하지 않고 하나의 대기열 단위로 등록합니다.</summary>
    internal MatchQueueCommandResult Enqueue(string queueKey, MatchQueueEntryRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return Failure(request.PartyId, MatchQueueCommandError.InvalidRequestId);
        }

        if (_requests.TryGetValue(request.RequestId, out var storedRequest))
        {
            return storedRequest.Matches(request)
                ? Replay(storedRequest.Result)
                : Failure(request.PartyId, MatchQueueCommandError.RequestIdConflict);
        }

        var validationError = ValidateEnqueueRequest(request);
        if (validationError is not MatchQueueCommandError.None)
        {
            return Store(request, Failure(request.PartyId, validationError));
        }

        if (_ticketsByPartyId.TryGetValue(request.PartyId, out var currentTicket))
        {
            var partyError = currentTicket.Status switch
            {
                MatchQueueTicketStatus.Queued => MatchQueueCommandError.PartyAlreadyQueued,
                MatchQueueTicketStatus.Matched => MatchQueueCommandError.PartyAlreadyMatched,
                _ => MatchQueueCommandError.None,
            };

            if (partyError is not MatchQueueCommandError.None)
            {
                return Store(request, Failure(request.PartyId, partyError));
            }
        }

        if (HasPlayerInAnotherCurrentTicket(request.PartyId, request.MemberPlayerIds))
        {
            return Store(request, Failure(request.PartyId, MatchQueueCommandError.PlayerAlreadyQueued));
        }

        // 호출자가 전달한 배열을 복사하여 외부 변경이 Grain 내부 상태에 영향을 주지 못하게 합니다.
        var ticket = new MatchQueueTicket(
            Guid.NewGuid(),
            queueKey,
            request.PartyId,
            request.LeaderPlayerId,
            request.MemberPlayerIds.ToArray(),
            MatchQueueTicketStatus.Queued,
            RoomId: null,
            DateTimeOffset.UtcNow);

        _ticketsByPartyId[request.PartyId] = ticket;
        _queuedPartyIds.Add(request.PartyId);

        var match = TryCreateMatch(queueKey);
        var currentResultTicket = CloneTicket(_ticketsByPartyId[request.PartyId]);
        return Store(request, Success(currentResultTicket, match));
    }

    /// <summary>리더만 아직 대기 중인 티켓을 취소할 수 있도록 처리합니다.</summary>
    internal MatchQueueCommandResult Cancel(CancelMatchQueueRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return Failure(request.PartyId, MatchQueueCommandError.InvalidRequestId);
        }

        if (_requests.TryGetValue(request.RequestId, out var storedRequest))
        {
            return storedRequest.Matches(request)
                ? Replay(storedRequest.Result)
                : Failure(request.PartyId, MatchQueueCommandError.RequestIdConflict);
        }

        if (request.PartyId == Guid.Empty)
        {
            return Store(request, Failure(request.PartyId, MatchQueueCommandError.InvalidPartyId));
        }

        if (request.LeaderPlayerId == Guid.Empty)
        {
            return Store(request, Failure(request.PartyId, MatchQueueCommandError.InvalidLeaderPlayerId));
        }

        if (!_ticketsByPartyId.TryGetValue(request.PartyId, out var ticket))
        {
            return Store(request, Failure(request.PartyId, MatchQueueCommandError.TicketNotFound));
        }

        if (ticket.LeaderPlayerId != request.LeaderPlayerId)
        {
            return Store(request, Failure(request.PartyId, MatchQueueCommandError.OnlyLeaderCanCancel));
        }

        var statusError = ticket.Status switch
        {
            MatchQueueTicketStatus.Matched => MatchQueueCommandError.TicketAlreadyMatched,
            MatchQueueTicketStatus.Cancelled => MatchQueueCommandError.TicketAlreadyCancelled,
            _ => MatchQueueCommandError.None,
        };

        if (statusError is not MatchQueueCommandError.None)
        {
            return Store(request, Failure(request.PartyId, statusError));
        }

        var cancelledTicket = ticket with
        {
            Status = MatchQueueTicketStatus.Cancelled,
            MemberPlayerIds = ticket.MemberPlayerIds.ToArray(),
        };

        _ticketsByPartyId[request.PartyId] = cancelledTicket;
        _queuedPartyIds.Remove(request.PartyId);

        return Store(request, Success(CloneTicket(cancelledTicket), match: null));
    }

    /// <summary>특정 파티 티켓의 방어적 복사본을 반환합니다.</summary>
    internal MatchQueueTicket? GetTicket(Guid partyId)
    {
        return _ticketsByPartyId.TryGetValue(partyId, out var ticket)
            ? CloneTicket(ticket)
            : null;
    }

    /// <summary>현재 대기 중인 티켓만 등록 순서대로 복사하여 반환합니다.</summary>
    internal MatchQueueSnapshot GetSnapshot(string queueKey)
    {
        var queuedTickets = _queuedPartyIds
            .Select(partyId => CloneTicket(_ticketsByPartyId[partyId]))
            .ToArray();

        return new MatchQueueSnapshot(queueKey, TargetPlayerCount, queuedTickets);
    }

    private static MatchQueueCommandError ValidateEnqueueRequest(MatchQueueEntryRequest request)
    {
        if (request.PartyId == Guid.Empty)
        {
            return MatchQueueCommandError.InvalidPartyId;
        }

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

        return request.MemberPlayerIds.Contains(request.LeaderPlayerId)
            ? MatchQueueCommandError.None
            : MatchQueueCommandError.LeaderNotMember;
    }

    private bool HasPlayerInAnotherCurrentTicket(Guid partyId, IEnumerable<Guid> memberPlayerIds)
    {
        var requestedPlayerIds = memberPlayerIds.ToHashSet();

        return _ticketsByPartyId.Values.Any(ticket =>
            ticket.PartyId != partyId
            && ticket.Status is MatchQueueTicketStatus.Queued or MatchQueueTicketStatus.Matched
            && ticket.MemberPlayerIds.Any(requestedPlayerIds.Contains));
    }

    /// <summary>
    /// 가장 오래 기다린 파티를 반드시 포함하면서 정확히 4명이 되는 가장 이른 조합을 찾습니다.
    /// </summary>
    private MatchAssignment? TryCreateMatch(string queueKey)
    {
        if (_queuedPartyIds.Count == 0)
        {
            return null;
        }

        var oldestPartyId = _queuedPartyIds[0];
        var oldestTicket = _ticketsByPartyId[oldestPartyId];
        var selectedPartyIds = new List<Guid> { oldestPartyId };
        var remainingPlayerCount = TargetPlayerCount - oldestTicket.MemberPlayerIds.Length;

        if (!TrySelectCombination(startIndex: 1, remainingPlayerCount, selectedPartyIds))
        {
            return null;
        }

        var roomId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var selectedPartyIdSet = selectedPartyIds.ToHashSet();
        var playerIds = selectedPartyIds
            .SelectMany(partyId => _ticketsByPartyId[partyId].MemberPlayerIds)
            .ToArray();

        foreach (var partyId in selectedPartyIds)
        {
            var ticket = _ticketsByPartyId[partyId];
            _ticketsByPartyId[partyId] = ticket with
            {
                Status = MatchQueueTicketStatus.Matched,
                RoomId = roomId,
                MemberPlayerIds = ticket.MemberPlayerIds.ToArray(),
            };
        }

        _queuedPartyIds.RemoveAll(selectedPartyIdSet.Contains);

        return new MatchAssignment(
            roomId,
            queueKey,
            selectedPartyIds.ToArray(),
            playerIds,
            createdAt);
    }

    /// <summary>
    /// 대기 순서대로 후보를 선택하는 깊이 우선 탐색으로 남은 인원을 정확히 채웁니다.
    /// </summary>
    private bool TrySelectCombination(
        int startIndex,
        int remainingPlayerCount,
        List<Guid> selectedPartyIds)
    {
        if (remainingPlayerCount == 0)
        {
            return true;
        }

        for (var index = startIndex; index < _queuedPartyIds.Count; index++)
        {
            var candidatePartyId = _queuedPartyIds[index];
            var candidatePlayerCount = _ticketsByPartyId[candidatePartyId].MemberPlayerIds.Length;
            if (candidatePlayerCount > remainingPlayerCount)
            {
                continue;
            }

            selectedPartyIds.Add(candidatePartyId);
            if (TrySelectCombination(index + 1, remainingPlayerCount - candidatePlayerCount, selectedPartyIds))
            {
                return true;
            }

            selectedPartyIds.RemoveAt(selectedPartyIds.Count - 1);
        }

        return false;
    }

    private MatchQueueCommandResult Store(
        MatchQueueEntryRequest request,
        MatchQueueCommandResult result)
    {
        _requests[request.RequestId] = StoredRequest.ForEnqueue(request, CloneResult(result));
        return result;
    }

    private MatchQueueCommandResult Store(
        CancelMatchQueueRequest request,
        MatchQueueCommandResult result)
    {
        _requests[request.RequestId] = StoredRequest.ForCancel(request, CloneResult(result));
        return result;
    }

    private MatchQueueCommandResult Failure(Guid partyId, MatchQueueCommandError error)
    {
        return new MatchQueueCommandResult(
            IsReplay: false,
            Error: error,
            Ticket: GetTicket(partyId),
            Match: null);
    }

    private static MatchQueueCommandResult Success(
        MatchQueueTicket ticket,
        MatchAssignment? match)
    {
        return new MatchQueueCommandResult(
            IsReplay: false,
            Error: MatchQueueCommandError.None,
            Ticket: ticket,
            Match: CloneMatch(match));
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

    /// <summary>동일 requestId가 같은 명령인지 판별하기 위해 최초 입력과 결과를 함께 보관합니다.</summary>
    private sealed record StoredRequest(
        MatchQueueCommandKind CommandKind,
        Guid PartyId,
        Guid LeaderPlayerId,
        Guid[] MemberPlayerIds,
        MatchQueueCommandResult Result)
    {
        internal static StoredRequest ForEnqueue(
            MatchQueueEntryRequest request,
            MatchQueueCommandResult result)
        {
            return new StoredRequest(
                MatchQueueCommandKind.Enqueue,
                request.PartyId,
                request.LeaderPlayerId,
                request.MemberPlayerIds?.ToArray() ?? [],
                result);
        }

        internal static StoredRequest ForCancel(
            CancelMatchQueueRequest request,
            MatchQueueCommandResult result)
        {
            return new StoredRequest(
                MatchQueueCommandKind.Cancel,
                request.PartyId,
                request.LeaderPlayerId,
                [],
                result);
        }

        internal bool Matches(MatchQueueEntryRequest request)
        {
            return CommandKind == MatchQueueCommandKind.Enqueue
                && PartyId == request.PartyId
                && LeaderPlayerId == request.LeaderPlayerId
                && MemberPlayerIds.SequenceEqual(request.MemberPlayerIds ?? []);
        }

        internal bool Matches(CancelMatchQueueRequest request)
        {
            return CommandKind == MatchQueueCommandKind.Cancel
                && PartyId == request.PartyId
                && LeaderPlayerId == request.LeaderPlayerId;
        }
    }

    private enum MatchQueueCommandKind
    {
        Enqueue = 0,
        Cancel = 1,
    }
}
