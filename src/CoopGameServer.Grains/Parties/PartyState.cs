using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.Grains.Parties;

/// <summary>
/// PartyGrain 활성화 한 개가 메모리에서 소유하는 파티 상태와 게임 규칙입니다.
/// </summary>
/// <remarks>
/// 현재 단계에는 영속 저장소가 없으므로 Silo 재시작이나 Grain 재활성화 뒤에는 이 상태가 사라집니다.
/// 외부 API를 추가하기 전에 PostgreSQL 기반 Orleans 저장소로 교체해야 합니다.
/// </remarks>
internal sealed class PartyState
{
    /// <summary>리더를 포함한 파티의 최대 멤버 수입니다.</summary>
    private const int MaxMembers = 4;

    private readonly List<Guid> _memberPlayerIds = [];
    private readonly Dictionary<Guid, ProcessedPartyRequest> _processedRequests = [];

    private PartyLifecycle? _lifecycle;
    private Guid? _leaderPlayerId;

    /// <summary>
    /// 최초 멤버를 리더로 지정하여 파티를 생성합니다.
    /// </summary>
    internal PartyCommandResult Create(Guid partyId, Guid requestId, Guid leaderPlayerId)
    {
        return Execute(
            partyId,
            requestId,
            PartyCommandKind.Create,
            leaderPlayerId,
            () =>
            {
                if (leaderPlayerId == Guid.Empty)
                {
                    return Failure(partyId, PartyCommandError.InvalidPlayerId);
                }

                if (_lifecycle == PartyLifecycle.Active)
                {
                    return Failure(partyId, PartyCommandError.PartyAlreadyExists);
                }

                if (_lifecycle == PartyLifecycle.Disbanded)
                {
                    return Failure(partyId, PartyCommandError.PartyIdCannotBeReused);
                }

                _lifecycle = PartyLifecycle.Active;
                _leaderPlayerId = leaderPlayerId;
                _memberPlayerIds.Add(leaderPlayerId);

                return Success(partyId);
            });
    }

    /// <summary>
    /// 현재 파티 상태를 복사해 반환합니다. 조회는 내부 상태와 처리 요청 기록을 바꾸지 않습니다.
    /// </summary>
    internal PartySnapshot? Get(Guid partyId) => CreateSnapshot(partyId);

    /// <summary>
    /// 정원과 중복 가입 규칙을 확인한 뒤 플레이어를 가입 순서의 끝에 추가합니다.
    /// </summary>
    internal PartyCommandResult Join(Guid partyId, Guid requestId, Guid playerId)
    {
        return Execute(
            partyId,
            requestId,
            PartyCommandKind.Join,
            playerId,
            () =>
            {
                if (playerId == Guid.Empty)
                {
                    return Failure(partyId, PartyCommandError.InvalidPlayerId);
                }

                var lifecycleError = ValidateActiveParty();
                if (lifecycleError is not PartyCommandError.None)
                {
                    return Failure(partyId, lifecycleError);
                }

                if (_memberPlayerIds.Contains(playerId))
                {
                    return Failure(partyId, PartyCommandError.MemberAlreadyJoined);
                }

                if (_memberPlayerIds.Count >= MaxMembers)
                {
                    return Failure(partyId, PartyCommandError.PartyFull);
                }

                _memberPlayerIds.Add(playerId);
                return Success(partyId);
            });
    }

    /// <summary>
    /// 멤버를 제거하고, 리더가 나갔다면 가입 순서상 첫 잔존 멤버에게 리더를 넘깁니다.
    /// </summary>
    internal PartyCommandResult Leave(Guid partyId, Guid requestId, Guid playerId)
    {
        return Execute(
            partyId,
            requestId,
            PartyCommandKind.Leave,
            playerId,
            () =>
            {
                if (playerId == Guid.Empty)
                {
                    return Failure(partyId, PartyCommandError.InvalidPlayerId);
                }

                var lifecycleError = ValidateActiveParty();
                if (lifecycleError is not PartyCommandError.None)
                {
                    return Failure(partyId, lifecycleError);
                }

                if (!_memberPlayerIds.Remove(playerId))
                {
                    return Failure(partyId, PartyCommandError.MemberNotFound);
                }

                if (_memberPlayerIds.Count == 0)
                {
                    Disband();
                }
                else if (_leaderPlayerId == playerId)
                {
                    // List가 가입 순서를 유지하므로 첫 번째 잔존 멤버를 결정적으로 선택할 수 있습니다.
                    _leaderPlayerId = _memberPlayerIds[0];
                }

                return Success(partyId);
            });
    }

    /// <summary>
    /// 현재 리더의 요청인지 확인한 뒤 멤버 목록을 비우고 파티를 해산합니다.
    /// </summary>
    internal PartyCommandResult Disband(Guid partyId, Guid requestId, Guid leaderPlayerId)
    {
        return Execute(
            partyId,
            requestId,
            PartyCommandKind.Disband,
            leaderPlayerId,
            () =>
            {
                if (leaderPlayerId == Guid.Empty)
                {
                    return Failure(partyId, PartyCommandError.InvalidPlayerId);
                }

                var lifecycleError = ValidateActiveParty();
                if (lifecycleError is not PartyCommandError.None)
                {
                    return Failure(partyId, lifecycleError);
                }

                if (_leaderPlayerId != leaderPlayerId)
                {
                    return Failure(partyId, PartyCommandError.OnlyLeaderCanDisband);
                }

                Disband();
                return Success(partyId);
            });
    }

    /// <summary>
    /// requestId의 최초 처리 결과를 저장하고 동일 요청에는 그 결과를 재사용합니다.
    /// </summary>
    private PartyCommandResult Execute(
        Guid partyId,
        Guid requestId,
        PartyCommandKind commandKind,
        Guid playerId,
        Func<PartyCommandResult> executeCommand)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

        if (requestId == Guid.Empty)
        {
            // 비어 있는 ID는 요청 기록의 키로 사용할 수 없으므로 저장하지 않습니다.
            return Failure(partyId, PartyCommandError.InvalidRequestId);
        }

        var signature = new PartyRequestSignature(commandKind, partyId, playerId);

        if (_processedRequests.TryGetValue(requestId, out var processedRequest))
        {
            if (processedRequest.Signature != signature)
            {
                return Failure(partyId, PartyCommandError.RequestIdConflict);
            }

            return processedRequest.Result with { IsReplay = true };
        }

        var result = executeCommand();
        _processedRequests.Add(requestId, new ProcessedPartyRequest(signature, result));
        return result;
    }

    private PartyCommandError ValidateActiveParty()
    {
        return _lifecycle switch
        {
            null => PartyCommandError.PartyNotCreated,
            PartyLifecycle.Disbanded => PartyCommandError.PartyDisbanded,
            _ => PartyCommandError.None,
        };
    }

    private void Disband()
    {
        _lifecycle = PartyLifecycle.Disbanded;
        _leaderPlayerId = null;
        _memberPlayerIds.Clear();
    }

    private PartyCommandResult Success(Guid partyId)
    {
        return new PartyCommandResult(
            IsReplay: false,
            Error: PartyCommandError.None,
            Party: CreateSnapshot(partyId));
    }

    private PartyCommandResult Failure(Guid partyId, PartyCommandError error)
    {
        return new PartyCommandResult(
            IsReplay: false,
            Error: error,
            Party: CreateSnapshot(partyId));
    }

    private PartySnapshot? CreateSnapshot(Guid partyId)
    {
        if (_lifecycle is null)
        {
            return null;
        }

        // 배열을 새로 만들어 호출자가 반환값을 수정해도 Grain의 실제 List가 바뀌지 않게 합니다.
        return new PartySnapshot(
            partyId,
            _lifecycle.Value,
            _leaderPlayerId,
            _memberPlayerIds.ToArray());
    }

    /// <summary>
    /// requestId와 함께 비교할 요청 본문입니다. 같은 키의 다른 본문을 충돌로 판별합니다.
    /// </summary>
    private readonly record struct PartyRequestSignature(
        PartyCommandKind CommandKind,
        Guid PartyId,
        Guid PlayerId);

    /// <summary>
    /// 최초 요청의 서명과 당시 반환 결과를 함께 보관합니다.
    /// </summary>
    private sealed record ProcessedPartyRequest(
        PartyRequestSignature Signature,
        PartyCommandResult Result);

    private enum PartyCommandKind
    {
        Create,
        Join,
        Leave,
        Disband,
    }
}
