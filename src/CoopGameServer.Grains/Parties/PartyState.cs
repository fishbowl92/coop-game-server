using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.Grains.Parties;

/// <summary>
/// PartyGrain 활성화 한 개가 메모리에서 소유하는 파티 상태와 게임 규칙입니다.
/// </summary>
/// <remarks>
/// 이 클래스는 생성·가입·탈퇴·해산 규칙만 판단합니다.
/// PostgreSQL 저장, 트랜잭션, requestId 멱등성 처리는 PartyGrain이 담당합니다.
/// </remarks>
internal sealed class PartyState
{
    /// <summary>리더를 포함한 파티의 최대 멤버 수입니다.</summary>
    private const int MaxMembers = 4;

    private readonly List<Guid> _memberPlayerIds = [];
    private PartyLifecycle? _lifecycle;
    private Guid? _leaderPlayerId;

    /// <summary>
    /// PostgreSQL에서 읽은 파티 상태로 메모리 규칙 객체를 복원합니다.
    /// </summary>
    internal static PartyState Restore(
        PartyLifecycle lifecycle,
        Guid? leaderPlayerId,
        IEnumerable<Guid> memberPlayerIds)
    {
        var state = new PartyState
        {
            _lifecycle = lifecycle,
            _leaderPlayerId = leaderPlayerId,
        };

        state._memberPlayerIds.AddRange(memberPlayerIds);
        return state;
    }

    /// <summary>
    /// DB 저장이 실패해도 기존 메모리 상태가 바뀌지 않도록 명령 적용 전 깊은 복사본을 만듭니다.
    /// </summary>
    internal PartyState Clone()
    {
        if (_lifecycle is null)
        {
            return new PartyState();
        }

        return Restore(_lifecycle.Value, _leaderPlayerId, _memberPlayerIds);
    }

    /// <summary>
    /// 최초 멤버를 리더로 지정하여 파티를 생성합니다.
    /// </summary>
    internal PartyCommandResult Create(Guid partyId, Guid leaderPlayerId)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

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
    }

    /// <summary>
    /// 현재 파티 상태를 복사해 반환합니다. 조회는 내부 상태와 처리 요청 기록을 바꾸지 않습니다.
    /// </summary>
    internal PartySnapshot? Get(Guid partyId) => CreateSnapshot(partyId);

    /// <summary>
    /// 정원과 중복 가입 규칙을 확인한 뒤 플레이어를 가입 순서의 끝에 추가합니다.
    /// </summary>
    internal PartyCommandResult Join(Guid partyId, Guid playerId)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

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
    }

    /// <summary>
    /// 멤버를 제거하고, 리더가 나갔다면 가입 순서상 첫 잔존 멤버에게 리더를 넘깁니다.
    /// </summary>
    internal PartyCommandResult Leave(Guid partyId, Guid playerId)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

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
    }

    /// <summary>
    /// 현재 리더의 요청인지 확인한 뒤 멤버 목록을 비우고 파티를 해산합니다.
    /// </summary>
    internal PartyCommandResult Disband(Guid partyId, Guid leaderPlayerId)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

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

    internal PartyCommandResult Failure(Guid partyId, PartyCommandError error)
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

}
