using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.Grains.GameRooms;

/// <summary>GameRoomGrain 한 개가 소유하는 방 상태와 멱등성 요청 기록입니다.</summary>
/// <remarks>
/// 이 클래스는 순수한 상태 전이 규칙만 담당합니다. PostgreSQL 저장과 PartyGrain 호출은
/// GameRoomGrain이 담당하여 외부 호출 또는 DB 저장 실패 시 기존 메모리 상태를 보존합니다.
/// </remarks>
internal sealed class GameRoomState
{
    /// <summary>MatchQueueState의 4인 정원과 같은 게임 방 참가자 수입니다.</summary>
    internal const int TargetPlayerCount = 4;

    /// <summary>GameDbContext의 queue_key 최대 길이와 같은 제한입니다.</summary>
    internal const int MaxQueueKeyLength = 100;

    private readonly Dictionary<Guid, GameRoomStoredRequest> _requests = [];
    private GameRoomSnapshot? _room;

    /// <summary>PostgreSQL에서 읽은 방과 최초 요청 결과들로 메모리 상태를 복원합니다.</summary>
    internal static GameRoomState Restore(
        GameRoomSnapshot? room,
        IEnumerable<GameRoomStoredRequest> requests)
    {
        var state = new GameRoomState
        {
            _room = CloneSnapshot(room),
        };

        foreach (var request in requests)
        {
            state._requests.Add(request.RequestId, request.Copy());
        }

        return state;
    }

    /// <summary>DB 저장 전 후보 상태를 안전하게 변경할 수 있도록 깊은 복사본을 만듭니다.</summary>
    internal GameRoomState Clone() => Restore(_room, GetStoredRequests());

    /// <summary>현재 방 상태의 방어적 복사본을 반환합니다.</summary>
    internal GameRoomSnapshot? Get() => CloneSnapshot(_room);

    /// <summary>PostgreSQL 동기화에 사용할 멱등성 요청 기록의 복사본을 반환합니다.</summary>
    internal GameRoomStoredRequest[] GetStoredRequests()
    {
        return _requests.Values
            .OrderBy(request => request.CreatedAt)
            .Select(request => request.Copy())
            .ToArray();
    }

    /// <summary>매칭 결과의 방 키·파티·4인 참가자 구성을 검증하고 Ready 방을 만듭니다.</summary>
    internal GameRoomCommandResult Create(
        Guid roomId,
        Guid requestId,
        MatchAssignment assignment)
    {
        var requestError = ValidateRequestId(requestId);
        if (requestError is not GameRoomCommandError.None)
        {
            return Failure(requestError);
        }

        // 계약은 null을 허용하지 않지만 잘못된 외부 직렬화 입력이 런타임 예외로 번지지 않게 방어합니다.
        if (assignment is null)
        {
            return Failure(GameRoomCommandError.InvalidRoomId);
        }

        if (_requests.TryGetValue(requestId, out var storedRequest))
        {
            return storedRequest.Matches(assignment)
                ? Replay(storedRequest.Result)
                : Failure(GameRoomCommandError.RequestIdConflict);
        }

        var validationError = ValidateAssignment(roomId, assignment);
        if (validationError is not GameRoomCommandError.None)
        {
            return StoreCreate(requestId, assignment, Failure(validationError));
        }

        if (_room is not null)
        {
            return StoreCreate(requestId, assignment, Failure(GameRoomCommandError.RoomAlreadyExists));
        }

        _room = new GameRoomSnapshot(
            roomId,
            assignment.QueueKey,
            GameRoomLifecycle.Ready,
            assignment.PartyIds.ToArray(),
            assignment.PlayerIds.ToArray(),
            assignment.CreatedAt,
            StartedAt: null,
            CompletedAt: null);

        return StoreCreate(requestId, assignment, Success());
    }

    /// <summary>Ready 방을 InGame으로 전환합니다. 실제 PartyGrain 호출은 이 후보 상태 저장 전에 수행됩니다.</summary>
    internal GameRoomCommandResult Start(Guid requestId, DateTimeOffset startedAt)
    {
        var requestError = ValidateRequestId(requestId);
        if (requestError is not GameRoomCommandError.None)
        {
            return Failure(requestError);
        }

        if (_requests.TryGetValue(requestId, out var storedRequest))
        {
            return storedRequest.CommandKind == GameRoomCommandKind.Start
                ? Replay(storedRequest.Result)
                : Failure(GameRoomCommandError.RequestIdConflict);
        }

        if (_room is null)
        {
            return StoreSimple(requestId, GameRoomCommandKind.Start, Failure(GameRoomCommandError.RoomNotCreated));
        }

        var lifecycleError = _room.Lifecycle switch
        {
            GameRoomLifecycle.Ready => GameRoomCommandError.None,
            GameRoomLifecycle.InGame => GameRoomCommandError.RoomAlreadyStarted,
            GameRoomLifecycle.Completed => GameRoomCommandError.RoomCompleted,
            _ => throw new InvalidOperationException("알 수 없는 게임 방 생명 주기 상태입니다."),
        };

        if (lifecycleError is not GameRoomCommandError.None)
        {
            return StoreSimple(requestId, GameRoomCommandKind.Start, Failure(lifecycleError));
        }

        _room = _room with
        {
            Lifecycle = GameRoomLifecycle.InGame,
            PartyIds = _room.PartyIds.ToArray(),
            PlayerIds = _room.PlayerIds.ToArray(),
            StartedAt = startedAt,
        };

        return StoreSimple(requestId, GameRoomCommandKind.Start, Success());
    }

    /// <summary>InGame 방을 Completed 최종 상태로 바꿉니다.</summary>
    internal GameRoomCommandResult Complete(Guid requestId, DateTimeOffset completedAt)
    {
        var requestError = ValidateRequestId(requestId);
        if (requestError is not GameRoomCommandError.None)
        {
            return Failure(requestError);
        }

        if (_requests.TryGetValue(requestId, out var storedRequest))
        {
            return storedRequest.CommandKind == GameRoomCommandKind.Complete
                ? Replay(storedRequest.Result)
                : Failure(GameRoomCommandError.RequestIdConflict);
        }

        if (_room is null)
        {
            return StoreSimple(requestId, GameRoomCommandKind.Complete, Failure(GameRoomCommandError.RoomNotCreated));
        }

        var lifecycleError = _room.Lifecycle switch
        {
            GameRoomLifecycle.Ready => GameRoomCommandError.RoomNotInGame,
            GameRoomLifecycle.InGame => GameRoomCommandError.None,
            GameRoomLifecycle.Completed => GameRoomCommandError.RoomCompleted,
            _ => throw new InvalidOperationException("알 수 없는 게임 방 생명 주기 상태입니다."),
        };

        if (lifecycleError is not GameRoomCommandError.None)
        {
            return StoreSimple(requestId, GameRoomCommandKind.Complete, Failure(lifecycleError));
        }

        _room = _room with
        {
            Lifecycle = GameRoomLifecycle.Completed,
            PartyIds = _room.PartyIds.ToArray(),
            PlayerIds = _room.PlayerIds.ToArray(),
            CompletedAt = completedAt,
        };

        return StoreSimple(requestId, GameRoomCommandKind.Complete, Success());
    }

    /// <summary>
    /// 외부 PartyGrain 전이에 실패했을 때 DB에 고정하지 않는 재시도 가능 결과를 만듭니다.
    /// </summary>
    internal GameRoomCommandResult PartyTransitionFailure(Guid partyId, PartyCommandError partyError)
    {
        return PartyFailure(GameRoomCommandError.PartyTransitionFailed, partyId, partyError);
    }

    /// <summary>매칭 결과와 실제 파티 멤버 구성이 다를 때 세부 파티 식별자를 포함해 반환합니다.</summary>
    internal GameRoomCommandResult PartyRosterFailure(Guid partyId)
    {
        return PartyFailure(GameRoomCommandError.PartyRosterMismatch, partyId, partyError: null);
    }

    private static GameRoomCommandError ValidateRequestId(Guid requestId)
    {
        return requestId == Guid.Empty
            ? GameRoomCommandError.InvalidRequestId
            : GameRoomCommandError.None;
    }

    private static GameRoomCommandError ValidateAssignment(Guid roomId, MatchAssignment assignment)
    {
        if (roomId == Guid.Empty || assignment is null || assignment.RoomId != roomId)
        {
            return GameRoomCommandError.InvalidRoomId;
        }

        if (string.IsNullOrWhiteSpace(assignment.QueueKey)
            || assignment.QueueKey.Length > MaxQueueKeyLength)
        {
            return GameRoomCommandError.InvalidQueueKey;
        }

        if (assignment.PartyIds is null
            || assignment.PartyIds.Length > TargetPlayerCount
            || assignment.PartyIds.Any(partyId => partyId == Guid.Empty)
            || assignment.PartyIds.Distinct().Count() != assignment.PartyIds.Length)
        {
            return GameRoomCommandError.InvalidPartyIds;
        }

        if (assignment.PlayerIds is null
            || assignment.PlayerIds.Length != TargetPlayerCount
            || assignment.PlayerIds.Any(playerId => playerId == Guid.Empty)
            || assignment.PlayerIds.Distinct().Count() != assignment.PlayerIds.Length)
        {
            return GameRoomCommandError.InvalidPlayerIds;
        }

        return GameRoomCommandError.None;
    }

    private GameRoomCommandResult StoreCreate(
        Guid requestId,
        MatchAssignment assignment,
        GameRoomCommandResult result)
    {
        _requests[requestId] = GameRoomStoredRequest.ForCreate(requestId, assignment, CloneResult(result));
        return result;
    }

    private GameRoomCommandResult StoreSimple(
        Guid requestId,
        GameRoomCommandKind commandKind,
        GameRoomCommandResult result)
    {
        _requests[requestId] = GameRoomStoredRequest.ForSimple(requestId, commandKind, CloneResult(result));
        return result;
    }

    private GameRoomCommandResult Success()
    {
        return new GameRoomCommandResult(
            IsReplay: false,
            Error: GameRoomCommandError.None,
            Room: Get(),
            FailedPartyId: null,
            PartyError: null);
    }

    private GameRoomCommandResult Failure(GameRoomCommandError error)
    {
        return new GameRoomCommandResult(
            IsReplay: false,
            Error: error,
            Room: Get(),
            FailedPartyId: null,
            PartyError: null);
    }

    private GameRoomCommandResult PartyFailure(
        GameRoomCommandError error,
        Guid partyId,
        PartyCommandError? partyError)
    {
        return new GameRoomCommandResult(
            IsReplay: false,
            Error: error,
            Room: Get(),
            FailedPartyId: partyId,
            PartyError: partyError);
    }

    private static GameRoomCommandResult Replay(GameRoomCommandResult result)
    {
        var copy = CloneResult(result);
        return copy with { IsReplay = true };
    }

    internal static GameRoomCommandResult CloneResult(GameRoomCommandResult result)
    {
        return result with { Room = CloneSnapshot(result.Room) };
    }

    internal static GameRoomSnapshot? CloneSnapshot(GameRoomSnapshot? room)
    {
        return room is null
            ? null
            : room with
            {
                PartyIds = room.PartyIds.ToArray(),
                PlayerIds = room.PlayerIds.ToArray(),
            };
    }
}

/// <summary>같은 requestId가 어떤 방 명령에 사용됐는지 구분합니다.</summary>
internal enum GameRoomCommandKind
{
    Create = 0,
    Start = 1,
    Complete = 2,
}

/// <summary>Silo 재시작 뒤에도 최초 방 명령 결과를 재생하기 위한 메모리 기록입니다.</summary>
internal sealed record GameRoomStoredRequest(
    Guid RequestId,
    GameRoomCommandKind CommandKind,
    MatchAssignment? CreateAssignment,
    GameRoomCommandResult Result,
    DateTimeOffset CreatedAt)
{
    internal static GameRoomStoredRequest ForCreate(
        Guid requestId,
        MatchAssignment assignment,
        GameRoomCommandResult result)
    {
        return new GameRoomStoredRequest(
            requestId,
            GameRoomCommandKind.Create,
            CloneAssignment(assignment),
            result,
            DateTimeOffset.UtcNow);
    }

    internal static GameRoomStoredRequest ForSimple(
        Guid requestId,
        GameRoomCommandKind commandKind,
        GameRoomCommandResult result)
    {
        return new GameRoomStoredRequest(
            requestId,
            commandKind,
            CreateAssignment: null,
            result,
            DateTimeOffset.UtcNow);
    }

    internal bool Matches(MatchAssignment assignment)
    {
        return CommandKind == GameRoomCommandKind.Create
            && CreateAssignment is { } storedAssignment
            && storedAssignment.RoomId == assignment.RoomId
            && string.Equals(storedAssignment.QueueKey, assignment.QueueKey, StringComparison.Ordinal)
            && storedAssignment.PartyIds.SequenceEqual(assignment.PartyIds ?? [])
            && storedAssignment.PlayerIds.SequenceEqual(assignment.PlayerIds ?? [])
            && storedAssignment.CreatedAt == assignment.CreatedAt;
    }

    internal GameRoomStoredRequest Copy()
    {
        return this with
        {
            CreateAssignment = CreateAssignment is null ? null : CloneAssignment(CreateAssignment),
            Result = GameRoomState.CloneResult(Result),
        };
    }

    private static MatchAssignment CloneAssignment(MatchAssignment assignment)
    {
        return assignment with
        {
            // 유효하지 않은 null 배열 요청도 최초 실패 결과와 함께 안전하게 복사할 수 있어야 합니다.
            PartyIds = assignment.PartyIds?.ToArray() ?? [],
            PlayerIds = assignment.PlayerIds?.ToArray() ?? [],
        };
    }
}
