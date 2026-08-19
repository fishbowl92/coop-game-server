using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.Grains.Parties;

/// <summary>
/// PartyGrain 활성화 한 개가 메모리에서 소유하는 파티 상태와 게임 규칙입니다.
/// </summary>
/// <remarks>
/// 이 클래스는 생성·가입·탈퇴·해산과 매칭 대기·게임 진행 상태 전이 규칙만 판단합니다.
/// PostgreSQL 저장, 트랜잭션, requestId 멱등성 처리는 PartyGrain이 담당합니다.
/// </remarks>
internal sealed class PartyState
{
    /// <summary>리더를 포함한 파티의 최대 멤버 수입니다.</summary>
    private const int MaxMembers = 4;

    private readonly List<Guid> _memberPlayerIds = [];
    private PartyLifecycle? _lifecycle;
    private Guid? _leaderPlayerId;
    private Guid? _currentRoomId;

    /// <summary>
    /// PostgreSQL에서 읽은 파티 상태로 메모리 규칙 객체를 복원합니다.
    /// </summary>
    internal static PartyState Restore(
        PartyLifecycle lifecycle,
        Guid? leaderPlayerId,
        IEnumerable<Guid> memberPlayerIds,
        Guid? currentRoomId)
    {
        var state = new PartyState
        {
            _lifecycle = lifecycle,
            _leaderPlayerId = leaderPlayerId,
            _currentRoomId = currentRoomId,
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

        return Restore(_lifecycle.Value, _leaderPlayerId, _memberPlayerIds, _currentRoomId);
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

        if (_lifecycle is PartyLifecycle.Active or PartyLifecycle.MatchQueued or PartyLifecycle.InGame)
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

    /// <summary>
    /// 현재 리더의 요청인지 확인하고 파티를 매칭 대기 상태로 바꿔 멤버 구성을 잠급니다.
    /// </summary>
    internal PartyCommandResult QueueForMatch(Guid partyId, Guid leaderPlayerId)
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
            return Failure(partyId, PartyCommandError.OnlyLeaderCanManageMatchmaking);
        }

        _lifecycle = PartyLifecycle.MatchQueued;
        return Success(partyId);
    }

    /// <summary>
    /// 현재 리더의 요청인지 확인하고 매칭 대기 상태를 로비 활성 상태로 되돌립니다.
    /// </summary>
    internal PartyCommandResult CancelMatchQueue(Guid partyId, Guid leaderPlayerId)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

        if (leaderPlayerId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPlayerId);
        }

        var lifecycleError = _lifecycle switch
        {
            null => PartyCommandError.PartyNotCreated,
            PartyLifecycle.Disbanded => PartyCommandError.PartyDisbanded,
            PartyLifecycle.Active => PartyCommandError.PartyNotMatchQueued,
            PartyLifecycle.InGame => PartyCommandError.PartyInGame,
            _ => PartyCommandError.None,
        };

        if (lifecycleError is not PartyCommandError.None)
        {
            return Failure(partyId, lifecycleError);
        }

        if (_leaderPlayerId != leaderPlayerId)
        {
            return Failure(partyId, PartyCommandError.OnlyLeaderCanManageMatchmaking);
        }

        _lifecycle = PartyLifecycle.Active;
        return Success(partyId);
    }

    /// <summary>
    /// 매칭 대기 중인 파티에 방 식별자를 연결하고 게임 진행 상태로 전환합니다.
    /// </summary>
    internal PartyCommandResult StartGame(Guid partyId, Guid roomId)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

        if (roomId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidRoomId);
        }

        var lifecycleError = _lifecycle switch
        {
            null => PartyCommandError.PartyNotCreated,
            PartyLifecycle.Disbanded => PartyCommandError.PartyDisbanded,
            PartyLifecycle.Active => PartyCommandError.PartyNotMatchQueued,
            PartyLifecycle.InGame => PartyCommandError.PartyInGame,
            _ => PartyCommandError.None,
        };

        if (lifecycleError is not PartyCommandError.None)
        {
            return Failure(partyId, lifecycleError);
        }

        _lifecycle = PartyLifecycle.InGame;
        _currentRoomId = roomId;
        return Success(partyId);
    }

    /// <summary>
    /// 현재 참가 중인 방과 같은 식별자인지 확인한 뒤 사전 구성 파티를 로비 상태로 되돌립니다.
    /// </summary>
    internal PartyCommandResult CompleteGame(Guid partyId, Guid roomId)
    {
        if (partyId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidPartyId);
        }

        if (roomId == Guid.Empty)
        {
            return Failure(partyId, PartyCommandError.InvalidRoomId);
        }

        var lifecycleError = _lifecycle switch
        {
            null => PartyCommandError.PartyNotCreated,
            PartyLifecycle.Disbanded => PartyCommandError.PartyDisbanded,
            PartyLifecycle.Active or PartyLifecycle.MatchQueued => PartyCommandError.PartyNotInGame,
            _ => PartyCommandError.None,
        };

        if (lifecycleError is not PartyCommandError.None)
        {
            return Failure(partyId, lifecycleError);
        }

        // 이전 게임의 늦게 도착한 완료 요청이 현재 게임을 끝내지 못하도록 방 식별자를 대조합니다.
        if (_currentRoomId != roomId)
        {
            return Failure(partyId, PartyCommandError.RoomIdMismatch);
        }

        _lifecycle = PartyLifecycle.Active;
        _currentRoomId = null;
        return Success(partyId);
    }

    private PartyCommandError ValidateActiveParty()
    {
        return _lifecycle switch
        {
            null => PartyCommandError.PartyNotCreated,
            PartyLifecycle.Active => PartyCommandError.None,
            PartyLifecycle.Disbanded => PartyCommandError.PartyDisbanded,
            PartyLifecycle.MatchQueued => PartyCommandError.PartyMatchQueued,
            PartyLifecycle.InGame => PartyCommandError.PartyInGame,
            _ => throw new InvalidOperationException("알 수 없는 파티 생명 주기 상태입니다."),
        };
    }

    private void Disband()
    {
        _lifecycle = PartyLifecycle.Disbanded;
        _leaderPlayerId = null;
        _currentRoomId = null;
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
            _memberPlayerIds.ToArray(),
            _currentRoomId);
    }

}
