using CoopGameServer.Domain.GameRooms;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.Grains.GameRooms;

/// <summary>GameRoomGrain 한 개가 소유하는 방 상태와 멱등성 요청 기록입니다.</summary>
/// <remarks>
/// 이 클래스는 순수한 상태 전이 규칙만 담당합니다. PostgreSQL 저장과 PartyGrain 호출은
/// GameRoomGrain이 담당하여 외부 호출 또는 DB 저장 실패 시 기존 메모리 상태를 보존합니다.
/// </remarks>
internal sealed partial class GameRoomState
{
    /// <summary>MatchQueueState의 4인 정원과 같은 게임 방 참가자 수입니다.</summary>
    internal const int TargetPlayerCount = 4;

    /// <summary>GameDbContext의 queue_key 최대 길이와 같은 제한입니다.</summary>
    internal const int MaxQueueKeyLength = 100;

    /// <summary>
    /// 현재 서버가 새 게임 방에 고정하는 보상 정책 버전입니다.
    /// 방 생성 뒤에는 값을 바꾸지 않아야 같은 경기 결과를 언제 처리해도 같은 정책을 선택할 수 있습니다.
    /// </summary>
    internal const int CurrentRewardPolicyVersion = 1;

    /// <summary>현재 새 방이 사용하는 참가자 전투 상태 규칙 버전입니다.</summary>
    internal const int CurrentCombatRuleVersion = GameRoomCombatRules.CurrentVersion;

    /// <summary>버전 1 참가자의 초기 체력입니다. 실제 공격 처리는 후속 단계에서 추가합니다.</summary>
    internal const int InitialPlayerHealth = GameRoomCombatRules.InitialPlayerHealth;

    private readonly Dictionary<Guid, GameRoomStoredRequest> _requests = [];
    private GameRoomSnapshot? _room;
    private readonly Dictionary<Guid, RoomPlayerConnection> _connections = [];

    /// <summary>PostgreSQL에서 읽은 방과 최초 요청 결과들로 메모리 상태를 복원합니다.</summary>
    internal static GameRoomState Restore(
        GameRoomSnapshot? room,
        IEnumerable<GameRoomStoredRequest> requests,
        IEnumerable<KeyValuePair<Guid, RoomPlayerConnection>>? connections = null)
    {
        var state = new GameRoomState
        {
            _room = CloneSnapshot(room),
        };

        foreach (var request in requests)
        {
            state._requests.Add(request.RequestId, NormalizeRestoredRequest(request, state._room));
        }

        foreach (var connection in connections ?? []) state._connections.Add(connection.Key, connection.Value);
        return state;
    }

    /// <summary>DB 저장 전 후보 상태를 안전하게 변경할 수 있도록 깊은 복사본을 만듭니다.</summary>
    internal GameRoomState Clone() => Restore(_room, GetStoredRequests(), _connections);

    /// <summary>공개 스냅샷과 분리해 저장 계층에만 전달하는 연결 상태입니다.</summary>
    internal RoomPlayerConnection GetConnection(Guid playerId) => _connections.GetValueOrDefault(playerId) ?? new();

    /// <summary>현재 방 상태의 방어적 복사본을 반환합니다.</summary>
    internal GameRoomSnapshot? Get()
    {
        var snapshot = CloneSnapshot(_room);
        return snapshot is null ? null : snapshot with
        {
            Players = snapshot.Players?.Select(player => player with
            {
                ConnectionStatus = (snapshot.Lifecycle == GameRoomLifecycle.Completed
                    ? GameRoomConnectionRules.Close(GetConnection(player.PlayerId)).Status
                    : GetConnection(player.PlayerId).Status).ToString(),
            }).ToArray(),
        };
    }

    /// <summary>후보 상태 복제에 사용할 전체 멱등성 요청 기록의 복사본을 반환합니다.</summary>
    internal GameRoomStoredRequest[] GetStoredRequests()
    {
        return _requests.Values
            .OrderBy(request => request.CreatedAt)
            .Select(request => request.Copy())
            .ToArray();
    }

    /// <summary>이번에 저장할 요청 한 건을 사전에서 찾고, 내부 배열까지 분리한 복사본을 반환합니다.</summary>
    /// <param name="requestId">찾을 요청 식별자입니다. 존재하지 않으면 null을 반환합니다.</param>
    internal GameRoomStoredRequest? GetStoredRequest(Guid requestId)
    {
        return _requests.TryGetValue(requestId, out var request) ? request.Copy() : null;
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
            CompletedAt: null,
            Outcome: GameOutcome.None,
            RewardPolicyVersion: CurrentRewardPolicyVersion,
            CombatRuleVersion: CurrentCombatRuleVersion,
            Players: assignment.PlayerIds.Select((playerId, order) => new PlayerCombatSnapshot(
                playerId, order, InitialPlayerHealth, InitialPlayerHealth,
                PlayerCombatStatus.Active, 0, null, null)).ToArray(),
            MaxWaves: GameRoomCombatRules.WaveCount,
            StateVersion: 1,
            InitialConnectDeadline: assignment.CreatedAt.AddSeconds(30));

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

        if (_room.StateVersion == long.MaxValue)
        {
            return StoreSimple(requestId, GameRoomCommandKind.Start, Failure(GameRoomCommandError.StateVersionExhausted));
        }

        // 웨이브 설정은 생성 시 고정된 버전으로 선택합니다. 참가자 체력·쿨다운을 다시 초기화하지 않습니다.
        var firstWave = GameRoomCombatRules.GetWave(_room.CombatRuleVersion, 1);
        _room = _room with
        {
            Lifecycle = GameRoomLifecycle.InGame,
            PartyIds = _room.PartyIds.ToArray(),
            PlayerIds = _room.PlayerIds.ToArray(),
            StartedAt = startedAt,
            CurrentWave = firstWave.WaveNumber,
            EnemyMaxHealth = firstWave.EnemyMaxHealth,
            EnemyCurrentHealth = firstWave.EnemyMaxHealth,
            StateVersion = checked(_room.StateVersion + 1),
        };

        return StoreSimple(requestId, GameRoomCommandKind.Start, Success());
    }

    /// <summary>InGame 방을 Completed 최종 상태로 바꿉니다.</summary>
    internal GameRoomCommandResult Complete(
        Guid requestId,
        GameOutcome outcome,
        DateTimeOffset completedAt)
    {
        var requestError = ValidateRequestId(requestId);
        if (requestError is not GameRoomCommandError.None)
        {
            return Failure(requestError);
        }

        if (_requests.TryGetValue(requestId, out var storedRequest))
        {
            return storedRequest.Matches(outcome)
                ? Replay(storedRequest.Result)
                : Failure(GameRoomCommandError.RequestIdConflict);
        }

        if (!IsFinalOutcome(outcome))
        {
            return StoreComplete(requestId, outcome, Failure(GameRoomCommandError.InvalidOutcome));
        }

        if (_room is null)
        {
            return StoreComplete(requestId, outcome, Failure(GameRoomCommandError.RoomNotCreated));
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
            return StoreComplete(requestId, outcome, Failure(lifecycleError));
        }

        if (_room.StateVersion == long.MaxValue)
        {
            return StoreComplete(requestId, outcome, Failure(GameRoomCommandError.StateVersionExhausted));
        }

        _room = _room with
        {
            Lifecycle = GameRoomLifecycle.Completed,
            PartyIds = _room.PartyIds.ToArray(),
            PlayerIds = _room.PlayerIds.ToArray(),
            CompletedAt = completedAt,
            Outcome = outcome,
            // 현재 관리자용 결과 지정 경로입니다. 적 체력이나 웨이브를 조작하여 승리를 꾸미지 않습니다.
            StateVersion = checked(_room.StateVersion + 1),
        };

        // 완료 응답을 만드는 시점부터 모든 활성 연결 자격을 폐기합니다.
        CloseConnections();

        return StoreComplete(requestId, outcome, Success());
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

    /// <summary>완료 명령에 사용할 수 있는 확정 결과인지 검사합니다.</summary>
    private static bool IsFinalOutcome(GameOutcome outcome)
    {
        return outcome is GameOutcome.Victory or GameOutcome.Defeat or GameOutcome.Cancelled;
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

    /// <summary>완료 결과까지 요청 원문에 포함해 같은 requestId의 내용 변경을 검출합니다.</summary>
    private GameRoomCommandResult StoreComplete(
        Guid requestId,
        GameOutcome outcome,
        GameRoomCommandResult result)
    {
        _requests[requestId] = GameRoomStoredRequest.ForComplete(
            requestId,
            outcome,
            CloneResult(result));
        return result;
    }

    /// <summary>
    /// 새 필드가 없던 이전 DB 기록을 복원할 때 정책 버전과 완료 결과를 안전한 값으로 보정합니다.
    /// 이전 완료 방은 실제 승패를 알 수 없으므로 승리나 패배를 추측하지 않고 Cancelled로 분류합니다.
    /// </summary>
    private static GameRoomStoredRequest NormalizeRestoredRequest(
        GameRoomStoredRequest request,
        GameRoomSnapshot? currentRoom)
    {
        var copy = request.Copy();
        if (copy.Result.Room is not { } resultRoom)
        {
            return copy;
        }

        var rewardPolicyVersion = resultRoom.RewardPolicyVersion > 0
            ? resultRoom.RewardPolicyVersion
            : currentRoom?.RewardPolicyVersion > 0
                ? currentRoom.RewardPolicyVersion
                : CurrentRewardPolicyVersion;
        var outcome = resultRoom.Lifecycle == GameRoomLifecycle.Completed
            ? resultRoom.Outcome is GameOutcome.None
                ? currentRoom?.Outcome is > GameOutcome.None
                    ? currentRoom.Outcome
                    : GameOutcome.Cancelled
                : resultRoom.Outcome
            : GameOutcome.None;

        return copy with
        {
            CompleteOutcome = copy.CommandKind == GameRoomCommandKind.Complete
                ? copy.CompleteOutcome is GameOutcome.None or null
                    ? outcome
                    : copy.CompleteOutcome
                : null,
            Result = copy.Result with
            {
                Room = resultRoom with
                {
                    Outcome = outcome,
                    RewardPolicyVersion = rewardPolicyVersion,
                },
            },
        };
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
                // 참가자 레코드의 필드는 불변 값이므로 배열만 분리하면 내부 상태를 보호할 수 있습니다.
                Players = room.Players?.ToArray(),
            };
    }
}

/// <summary>같은 requestId가 어떤 방 명령에 사용됐는지 구분합니다.</summary>
internal enum GameRoomCommandKind
{
    Create = 0,
    Start = 1,
    Complete = 2,
    Combat = 3,
    Connection = 4,
}

/// <summary>Silo 재시작 뒤에도 최초 방 명령 결과를 재생하기 위한 메모리 기록입니다.</summary>
internal sealed record GameRoomStoredRequest(
    Guid RequestId,
    GameRoomCommandKind CommandKind,
    MatchAssignment? CreateAssignment,
    GameOutcome? CompleteOutcome,
    GameRoomCommandResult Result,
    DateTimeOffset CreatedAt,
    GameRoomCombatCommand? CombatCommand = null,
    GameRoomCombatError CombatError = GameRoomCombatError.None,
    DateTimeOffset? RetryAt = null,
    GameRoomConnectionCommand? ConnectionCommand = null,
    GameRoomConnectionResult? ConnectionResult = null)
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
            CompleteOutcome: null,
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
            CompleteOutcome: null,
            result,
            DateTimeOffset.UtcNow);
    }

    /// <summary>결과를 포함한 완료 명령의 최초 요청과 응답을 저장합니다.</summary>
    internal static GameRoomStoredRequest ForComplete(
        Guid requestId,
        GameOutcome outcome,
        GameRoomCommandResult result)
    {
        return new GameRoomStoredRequest(
            requestId,
            GameRoomCommandKind.Complete,
            CreateAssignment: null,
            CompleteOutcome: outcome,
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

    /// <summary>완료 요청이 최초 요청과 같은 결과를 가졌는지 검사합니다.</summary>
    internal bool Matches(GameOutcome outcome)
    {
        return CommandKind == GameRoomCommandKind.Complete
            && CompleteOutcome == outcome;
    }

    internal GameRoomStoredRequest Copy()
    {
        return this with
        {
            CreateAssignment = CreateAssignment is null ? null : CloneAssignment(CreateAssignment),
            Result = GameRoomState.CloneResult(Result),
            ConnectionResult = ConnectionResult is null ? null : ConnectionResult with { Room = GameRoomState.CloneSnapshot(ConnectionResult.Room) },
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
