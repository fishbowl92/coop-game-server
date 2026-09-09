using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.GrainContracts.Players;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.GameRooms;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Grains.GameRooms;

/// <summary>
/// 한 게임 방의 4인 참가자, 시작·완료 상태와 파티 복귀 규칙을 순차 처리하는 Orleans Grain입니다.
/// </summary>
/// <remarks>
/// PostgreSQL 트랜잭션은 GameRoomGrain 내부 상태와 멱등성 결과를 한 번에 저장합니다.
/// PartyGrain은 별도 Grain·별도 트랜잭션이므로 분산 트랜잭션을 사용하지 않고,
/// 결정적인 하위 requestId와 현재 상태 확인으로 중간 실패 뒤 재시도 시 같은 결과로 수렴시킵니다.
/// </remarks>
public sealed class GameRoomGrain(
    IDbContextFactory<GameDbContext> dbContextFactory,
    TimeProvider timeProvider)
    : Grain, IGameRoomGrain
{
    private const int InitialRetryDelaySeconds = 5;
    private const int MaximumRetryDelaySeconds = 60;

    // 사람이 DB JSON을 읽을 때 숫자보다 Ready·Start 같은 이름이 보이도록 열거형을 문자열로 저장합니다.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private GameRoomState _state = new();

    /// <summary>Grain 활성화 시 PostgreSQL에서 방 상태와 최초 요청 결과를 복원합니다.</summary>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var roomId = this.GetPrimaryKey();
        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var roomRecord = await gameDbContext.GameRooms
            .AsNoTracking()
            .SingleOrDefaultAsync(room => room.RoomId == roomId, cancellationToken);
        var requestRecords = await gameDbContext.GameRoomRequests
            .AsNoTracking()
            .Where(request => request.RoomId == roomId)
            .OrderBy(request => request.CreatedAt)
            .ToArrayAsync(cancellationToken);

        var participants = await gameDbContext.GameRoomPlayers.AsNoTracking()
            .Where(player => player.RoomId == roomId)
            .OrderBy(player => player.PlayerOrder)
            .ToArrayAsync(cancellationToken);
        var restoredSnapshot = roomRecord is null ? null : RestoreSnapshot(roomRecord, participants);
        if (restoredSnapshot is not null)
        {
            ValidateParticipants(restoredSnapshot);
        }

        _state = GameRoomState.Restore(
            restoredSnapshot,
            requestRecords.Select(RestoreStoredRequest));

        await base.OnActivateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GameRoomCommandResult> CreateAsync(Guid requestId, MatchAssignment assignment)
    {
        var candidateState = _state.Clone();
        var result = candidateState.Create(this.GetPrimaryKey(), requestId, assignment);

        // Guid.Empty 요청은 멱등성 PK로 저장할 수 없고, 재생할 가치도 없으므로 메모리 결과만 반환합니다.
        if (requestId == Guid.Empty || result.IsReplay)
        {
            return result;
        }

        await PersistCandidateStateAsync(candidateState, requestId);
        return result;
    }

    /// <inheritdoc />
    public Task<GameRoomSnapshot?> GetAsync()
    {
        return Task.FromResult(_state.Get());
    }

    /// <inheritdoc />
    public async Task<GameRoomCommandResult> StartAsync(Guid requestId)
    {
        var currentRoom = _state.Get();
        var candidateState = _state.Clone();
        var result = candidateState.Start(requestId, timeProvider.GetUtcNow());

        if (requestId == Guid.Empty || result.IsReplay)
        {
            return result;
        }

        if (result.Error is GameRoomCommandError.None)
        {
            var partyFailure = await StartPreformedPartiesAsync(
                currentRoom ?? throw new InvalidOperationException("시작할 게임 방 상태가 없습니다."),
                requestId);
            if (partyFailure is not null)
            {
                // 외부 상태가 복구된 뒤 같은 요청을 다시 시도할 수 있도록 이 실패는 DB에 고정하지 않습니다.
                return partyFailure;
            }
        }

        await PersistCandidateStateAsync(candidateState, requestId);
        return result;
    }

    /// <inheritdoc />
    public async Task<GameRoomCommandResult> CompleteAsync(Guid requestId, GameOutcome outcome)
    {
        var currentRoom = _state.Get();
        var candidateState = _state.Clone();
        var result = candidateState.Complete(requestId, outcome, timeProvider.GetUtcNow());

        if (requestId == Guid.Empty)
        {
            return result;
        }

        if (result.IsReplay)
        {
            // 최초 완료에서 방 DB 저장까지 성공하고 Queue 해제 응답만 유실됐을 수 있습니다.
            // 같은 requestId 재시도에서도 Queue의 완료 상태를 다시 확인해 최종 상태로 수렴시킵니다.
            if (result.Error is GameRoomCommandError.None)
            {
                await EnsureMatchTicketsCompletedAsync(
                    currentRoom ?? throw new InvalidOperationException("완료된 게임 방 상태가 없습니다."),
                    requestId);
                await FinalizeCompletedRoomAsync();
            }

            return result;
        }

        if (result.Error is GameRoomCommandError.None)
        {
            var partyFailure = await CompletePreformedPartiesAsync(
                currentRoom ?? throw new InvalidOperationException("완료할 게임 방 상태가 없습니다."),
                requestId);
            if (partyFailure is not null)
            {
                return partyFailure;
            }
        }

        await PersistCandidateStateAsync(candidateState, requestId);

        if (result.Error is GameRoomCommandError.None)
        {
            // 방 완료가 PostgreSQL에 확정된 뒤에만 참가자를 현재 매칭에서 해제합니다.
            // 반대 순서라면 방은 아직 InGame인데 같은 플레이어가 새 방에 들어갈 수 있습니다.
            await EnsureMatchTicketsCompletedAsync(
                candidateState.Get() ?? throw new InvalidOperationException("완료된 게임 방 상태가 없습니다."),
                requestId);
            await FinalizeCompletedRoomAsync();
        }

        return result;
    }

    /// <inheritdoc />
    public async Task FinalizeCompletedRoomAsync()
    {
        var room = _state.Get();
        if (room is null || room.Lifecycle is not GameRoomLifecycle.Completed)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        await using var readContext = await dbContextFactory.CreateDbContextAsync();
        var pendingResults = await readContext.GameResults
            .AsNoTracking()
            .Where(result => result.RoomId == room.RoomId
                && (result.DeliveryStatus == GameResultDeliveryStatus.Pending
                    || (result.DeliveryStatus == GameResultDeliveryStatus.PendingRetry
                        && (result.NextAttemptAt == null || result.NextAttemptAt <= now))))
            .OrderBy(result => result.PlayerId)
            .ToArrayAsync();

        foreach (var pendingResult in pendingResults)
        {
            if (!room.PlayerIds.Contains(pendingResult.PlayerId))
            {
                await PersistTerminalFailureAsync(
                    pendingResult,
                    "PlayerNotInRoom",
                    timeProvider.GetUtcNow());
                continue;
            }

            if (pendingResult.RewardPolicyVersion != room.RewardPolicyVersion)
            {
                await PersistTerminalFailureAsync(
                    pendingResult,
                    "RewardPolicyVersionMismatch",
                    timeProvider.GetUtcNow());
                continue;
            }

            PlayerRewardCommandResult playerResult;
            try
            {
                var player = GrainFactory.GetGrain<IPlayerGrain>(pendingResult.PlayerId);
                playerResult = await player.CompleteGameAsync(
                    new CompletePlayerGameCommand(
                        pendingResult.RewardRequestId,
                        room.RoomId,
                        room.QueueKey,
                        room.Outcome,
                        pendingResult.RewardPolicyVersion));
            }
            catch (Exception exception) when (IsTransientDeliveryException(exception))
            {
                var failedAt = timeProvider.GetUtcNow();
                var retryDelay = CalculateRetryDelay(pendingResult.AttemptCount + 1);
                await PersistRetryAsync(
                    pendingResult,
                    exception.GetType().Name,
                    failedAt + retryDelay,
                    failedAt);
                continue;
            }

            await PersistPlayerResultAsync(
                pendingResult,
                playerResult,
                timeProvider.GetUtcNow());
        }
    }

    /// <summary>PlayerGrain의 정상·업무 거부 결과를 해당 Player의 전달 행 하나에 저장합니다.</summary>
    private async Task PersistPlayerResultAsync(
        GameResultRecord pendingResult,
        PlayerRewardCommandResult playerResult,
        DateTimeOffset updatedAt)
    {
        ValidatePlayerResult(pendingResult, playerResult);

        await UpdateGameResultAsync(
            pendingResult.RoomId,
            pendingResult.PlayerId,
            trackedResult =>
            {
                switch (playerResult.Status)
                {
                    case PlayerRewardCommandStatus.Applied:
                        trackedResult.MarkApplied(updatedAt);
                        break;
                    case PlayerRewardCommandStatus.NoReward:
                        trackedResult.MarkNoReward(updatedAt);
                        break;
                    case PlayerRewardCommandStatus.Rejected:
                        trackedResult.MarkTerminalFailure(playerResult.Error.ToString(), updatedAt);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"지원하지 않는 Player 결과 상태입니다: {playerResult.Status}");
                }
            });
    }

    /// <summary>계약·데이터 오류를 자동 재시도하지 않는 최종 실패로 저장합니다.</summary>
    private Task PersistTerminalFailureAsync(
        GameResultRecord pendingResult,
        string errorCode,
        DateTimeOffset updatedAt)
    {
        return UpdateGameResultAsync(
            pendingResult.RoomId,
            pendingResult.PlayerId,
            trackedResult => trackedResult.MarkTerminalFailure(errorCode, updatedAt));
    }

    /// <summary>일시 장애와 계산된 다음 시도 시각을 저장합니다.</summary>
    private Task PersistRetryAsync(
        GameResultRecord pendingResult,
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset updatedAt)
    {
        return UpdateGameResultAsync(
            pendingResult.RoomId,
            pendingResult.PlayerId,
            trackedResult => trackedResult.ScheduleRetry(errorCode, nextAttemptAt, updatedAt));
    }

    /// <summary>한 Player의 결과 행만 새 DbContext에서 읽고 변경하여 부분 성공을 즉시 보존합니다.</summary>
    private async Task UpdateGameResultAsync(
        Guid roomId,
        Guid playerId,
        Action<GameResultRecord> update)
    {
        await using var updateContext = await dbContextFactory.CreateDbContextAsync();
        var trackedResult = await updateContext.GameResults.SingleAsync(
            result => result.RoomId == roomId && result.PlayerId == playerId);

        update(trackedResult);
        await updateContext.SaveChangesAsync();
    }

    /// <summary>PlayerGrain 응답이 상태별 불변 조건과 요청 식별자를 지키는지 확인합니다.</summary>
    private static void ValidatePlayerResult(
        GameResultRecord pendingResult,
        PlayerRewardCommandResult playerResult)
    {
        var isValid = playerResult.Status switch
        {
            PlayerRewardCommandStatus.Applied =>
                playerResult.Error is PlayerRewardCommandError.None
                && playerResult.Receipt is not null
                && playerResult.Receipt.RequestId == pendingResult.RewardRequestId
                && playerResult.Receipt.PlayerId == pendingResult.PlayerId,
            PlayerRewardCommandStatus.NoReward =>
                playerResult.Error is PlayerRewardCommandError.None
                && playerResult.Receipt is null,
            PlayerRewardCommandStatus.Rejected =>
                playerResult.Error is not PlayerRewardCommandError.None
                && Enum.IsDefined(playerResult.Error)
                && playerResult.Receipt is null,
            _ => false,
        };

        if (!isValid)
        {
            throw new InvalidOperationException(
                $"Player {pendingResult.PlayerId}의 게임 결과 전달 응답이 계약 불변 조건을 위반했습니다.");
        }
    }

    /// <summary>자동 재시도로 복구할 가능성이 있는 DB·시간 제한·Orleans 통신 예외만 분류합니다.</summary>
    private static bool IsTransientDeliveryException(Exception exception)
    {
        return exception is TimeoutException
            or DbException
            or SiloUnavailableException
            or OrleansMessageRejectionException
            or GatewayTooBusyException;
    }

    /// <summary>첫 5초에서 시작해 10·20·40초로 늘고 최대 60초를 넘지 않는 Backoff를 계산합니다.</summary>
    private static TimeSpan CalculateRetryDelay(int attemptNumber)
    {
        var safeAttemptNumber = Math.Max(attemptNumber, 1);
        var shift = Math.Min(safeAttemptNumber - 1, 4);
        var seconds = Math.Min(
            InitialRetryDelaySeconds * (1 << shift),
            MaximumRetryDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 완료된 방의 Queue Grain에 결정적인 하위 요청을 보내 모든 Matched 티켓을 Completed로 전환합니다.
    /// </summary>
    private async Task EnsureMatchTicketsCompletedAsync(GameRoomSnapshot room, Guid roomRequestId)
    {
        var queue = GrainFactory.GetGrain<IMatchQueueGrain>(room.QueueKey);
        var queueRequestId = CreateRelatedRequestId(roomRequestId, room.RoomId, operationMarker: 3);
        var queueResult = await queue.CompleteMatchAsync(
            new CompleteMatchQueueRequest(queueRequestId, room.RoomId));

        if (queueResult.Error is not MatchQueueCommandError.None)
        {
            // 방 완료는 이미 영속화됐을 수 있으므로 오류를 감추지 않습니다.
            // 호출자가 같은 GameRoom requestId로 재시도하면 이 하위 요청도 같은 ID로 다시 실행됩니다.
            throw new InvalidOperationException(
                $"게임 방 {room.RoomId}의 매칭 티켓 완료 처리에 실패했습니다: {queueResult.Error}");
        }
    }

    /// <summary>모든 사전 구성 파티가 이 방에 들어갈 수 있는지 확인한 뒤 InGame으로 전환합니다.</summary>
    private async Task<GameRoomCommandResult?> StartPreformedPartiesAsync(
        GameRoomSnapshot room,
        Guid roomRequestId)
    {
        var roomPlayerIds = room.PlayerIds.ToHashSet();
        var pendingTransitions = new List<(Guid PartyId, IPartyGrain Party)>();

        // 먼저 모든 파티를 검사하여 첫 파티를 바꾼 뒤 두 번째 파티의 단순 검증 오류를 발견하는 일을 줄입니다.
        foreach (var partyId in room.PartyIds)
        {
            var party = GrainFactory.GetGrain<IPartyGrain>(partyId);
            var snapshot = await party.GetAsync();
            if (snapshot is null)
            {
                return _state.PartyTransitionFailure(partyId, PartyCommandError.PartyNotCreated);
            }

            if (snapshot.MemberPlayerIds.Any(playerId => !roomPlayerIds.Contains(playerId)))
            {
                return _state.PartyRosterFailure(partyId);
            }

            switch (snapshot.Lifecycle)
            {
                case PartyLifecycle.MatchQueued:
                    pendingTransitions.Add((partyId, party));
                    break;
                case PartyLifecycle.InGame when snapshot.CurrentRoomId == room.RoomId:
                    // 이전 시도에서 파티 전이는 성공하고 방 DB 저장만 실패한 경우 이미 적용된 것으로 봅니다.
                    break;
                case PartyLifecycle.InGame:
                    return _state.PartyTransitionFailure(partyId, PartyCommandError.RoomIdMismatch);
                case PartyLifecycle.Active:
                    return _state.PartyTransitionFailure(partyId, PartyCommandError.PartyNotMatchQueued);
                case PartyLifecycle.Disbanded:
                    return _state.PartyTransitionFailure(partyId, PartyCommandError.PartyDisbanded);
                default:
                    throw new InvalidOperationException("알 수 없는 파티 생명 주기 상태입니다.");
            }
        }

        foreach (var (partyId, party) in pendingTransitions)
        {
            var partyRequestId = CreatePartyRequestId(roomRequestId, partyId, operationMarker: 1);
            var partyResult = await party.StartGameAsync(partyRequestId, room.RoomId);
            if (partyResult.Error is not PartyCommandError.None)
            {
                return _state.PartyTransitionFailure(partyId, partyResult.Error);
            }
        }

        return null;
    }

    /// <summary>게임 중인 사전 구성 파티를 멤버 그대로 Active 로비 상태로 되돌립니다.</summary>
    private async Task<GameRoomCommandResult?> CompletePreformedPartiesAsync(
        GameRoomSnapshot room,
        Guid roomRequestId)
    {
        var pendingTransitions = new List<(Guid PartyId, IPartyGrain Party)>();

        foreach (var partyId in room.PartyIds)
        {
            var party = GrainFactory.GetGrain<IPartyGrain>(partyId);
            var snapshot = await party.GetAsync();
            if (snapshot is null)
            {
                return _state.PartyTransitionFailure(partyId, PartyCommandError.PartyNotCreated);
            }

            switch (snapshot.Lifecycle)
            {
                case PartyLifecycle.InGame when snapshot.CurrentRoomId == room.RoomId:
                    pendingTransitions.Add((partyId, party));
                    break;
                case PartyLifecycle.Active:
                    // 이전 시도에서 Party 완료는 성공하고 GameRoom DB 저장만 실패한 경우입니다.
                    break;
                case PartyLifecycle.InGame:
                    return _state.PartyTransitionFailure(partyId, PartyCommandError.RoomIdMismatch);
                case PartyLifecycle.MatchQueued:
                    return _state.PartyTransitionFailure(partyId, PartyCommandError.PartyNotInGame);
                case PartyLifecycle.Disbanded:
                    return _state.PartyTransitionFailure(partyId, PartyCommandError.PartyDisbanded);
                default:
                    throw new InvalidOperationException("알 수 없는 파티 생명 주기 상태입니다.");
            }
        }

        foreach (var (partyId, party) in pendingTransitions)
        {
            var partyRequestId = CreatePartyRequestId(roomRequestId, partyId, operationMarker: 2);
            var partyResult = await party.CompleteGameAsync(partyRequestId, room.RoomId);
            if (partyResult.Error is not PartyCommandError.None)
            {
                return _state.PartyTransitionFailure(partyId, partyResult.Error);
            }
        }

        return null;
    }

    /// <summary>
    /// 방 요청·파티·작업 종류에서 항상 같은 하위 requestId를 만들어 중간 실패 후 호출을 안전하게 재시도합니다.
    /// </summary>
    private static Guid CreatePartyRequestId(
        Guid roomRequestId,
        Guid partyId,
        byte operationMarker)
    {
        return CreateRelatedRequestId(roomRequestId, partyId, operationMarker);
    }

    /// <summary>상위 방 요청·대상 식별자·작업 종류로 항상 같은 하위 requestId를 만듭니다.</summary>
    private static Guid CreateRelatedRequestId(
        Guid roomRequestId,
        Guid targetId,
        byte operationMarker)
    {
        Span<byte> source = stackalloc byte[33];
        roomRequestId.TryWriteBytes(source[..16]);
        targetId.TryWriteBytes(source.Slice(16, 16));
        source[32] = operationMarker;

        var hash = SHA256.HashData(source);
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>후보 방 상태와 이번에 새로 만든 요청 결과 한 건을 같은 트랜잭션으로 저장합니다.</summary>
    /// <param name="candidateState">명령을 적용했지만 아직 확정하지 않은 메모리 복사본입니다.</param>
    /// <param name="requestId">이번 명령의 식별자입니다. 과거 요청 전체를 다시 저장하지 않고 이 기록만 찾습니다.</param>
    private async Task PersistCandidateStateAsync(GameRoomState candidateState, Guid requestId)
    {
        // 같은 키의 다른 명령은 충돌 응답일 뿐 새 기록이 아닙니다. 최초 결과를 덮어쓰면 안 됩니다.
        // null 배정 정보처럼 상태 규칙에서 기록하지 않는 입력 역시 DB 쓰기를 발생시키지 않습니다.
        if (_state.GetStoredRequest(requestId) is not null
            || candidateState.GetStoredRequest(requestId) is not { } newRequest)
        {
            return;
        }

        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync();
        await using var transaction = await gameDbContext.Database.BeginTransactionAsync();

        await SynchronizeStateAsync(gameDbContext, this.GetPrimaryKey(), candidateState);
        // Append-only(추가 전용): 이미 확정된 요청 행은 삭제·갱신하지 않습니다.
        // 예상하지 못한 DB 키 충돌도 삭제로 우회하지 않고 트랜잭션 실패로 드러냅니다.
        gameDbContext.GameRoomRequests.Add(CreateRequestRecord(this.GetPrimaryKey(), newRequest));
        await gameDbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // 방 상태와 요청 결과가 모두 저장된 뒤에만 후보를 채택합니다. 실패하면 기존 메모리가 유지됩니다.
        _state = candidateState;
    }

    /// <summary>현재 roomId의 방 상태와 필요한 결과 전달 행을 후보 상태로 맞춥니다.</summary>
    private static async Task SynchronizeStateAsync(
        GameDbContext gameDbContext,
        Guid roomId,
        GameRoomState state)
    {
        var snapshot = state.Get();
        var existingRoom = await gameDbContext.GameRooms
            .SingleOrDefaultAsync(room => room.RoomId == roomId);

        if (snapshot is null)
        {
            if (existingRoom is not null)
            {
                gameDbContext.GameRooms.Remove(existingRoom);
            }
        }
        else if (existingRoom is null)
        {
            gameDbContext.GameRooms.Add(new GameRoomRecord(
                snapshot.RoomId,
                snapshot.QueueKey,
                (int)snapshot.Lifecycle,
                snapshot.PartyIds,
                snapshot.PlayerIds,
                snapshot.CreatedAt,
                snapshot.StartedAt,
                snapshot.CompletedAt,
                (int)snapshot.Outcome,
                snapshot.RewardPolicyVersion,
                snapshot.CombatRuleVersion,
                snapshot.CurrentWave, snapshot.MaxWaves, snapshot.EnemyMaxHealth,
                snapshot.EnemyCurrentHealth, snapshot.StateVersion, snapshot.EnemyAttackSequence));
        }
        else
        {
            existingRoom.Update(
                (int)snapshot.Lifecycle,
                snapshot.PartyIds,
                snapshot.PlayerIds,
                snapshot.StartedAt,
                snapshot.CompletedAt,
                (int)snapshot.Outcome);
            existingRoom.UpdateCombatProgress(snapshot.CurrentWave, snapshot.MaxWaves,
                snapshot.EnemyMaxHealth, snapshot.EnemyCurrentHealth, snapshot.StateVersion, snapshot.EnemyAttackSequence);
        }

        // 방 완료와 네 플레이어의 결과 전달 대기 행은 반드시 같은 Transaction으로 저장합니다.
        // 방만 Completed가 되고 결과 행이 빠지면 이후 복구 서비스가 전달 대상을 찾을 수 없습니다.
        if (snapshot is not null)
        {
            await SynchronizeParticipantsAsync(gameDbContext, snapshot, existingRoom is null);
            await SynchronizeGameResultsAsync(gameDbContext, snapshot);
        }
    }

    /// <summary>새 방에는 네 참가자를 추가하고, 기존 방은 구성 검증 후 바뀐 값만 저장합니다.</summary>
    private static async Task SynchronizeParticipantsAsync(
        GameDbContext context, GameRoomSnapshot snapshot, bool isNewRoom)
    {
        var players = ValidateParticipants(snapshot);
        var records = await context.GameRoomPlayers
            .Where(player => player.RoomId == snapshot.RoomId)
            .OrderBy(player => player.PlayerOrder)
            .ToArrayAsync();

        if (isNewRoom)
        {
            if (records.Length != 0)
            {
                throw new InvalidOperationException("새 방에 기존 참가자 행이 존재합니다.");
            }

            foreach (var player in players)
            {
                context.GameRoomPlayers.Add(new GameRoomPlayerRecord(snapshot.RoomId,
                    player.PlayerId, player.PlayerOrder, player.MaxHealth, player.CurrentHealth,
                    (int)player.CombatStatus, player.LastAcceptedCommandSequence,
                    player.BasicAttackReadyAt, player.SkillReadyAt));
            }

            return;
        }

        if (records.Length != players.Length
            || !records.Select(record => record.PlayerId).SequenceEqual(snapshot.PlayerIds)
            || !records.Select(record => record.PlayerOrder).SequenceEqual(Enumerable.Range(0, 4)))
        {
            // 누락된 행을 조용히 새 체력으로 복원하면 진행 상태를 잃습니다. 불일치는 저장 실패로 드러냅니다.
            throw new InvalidOperationException("저장된 방 참가자 구성 또는 순서가 후보 상태와 다릅니다.");
        }

        foreach (var player in players)
        {
            records[player.PlayerOrder].Update(player.MaxHealth, player.CurrentHealth,
                (int)player.CombatStatus, player.LastAcceptedCommandSequence,
                player.BasicAttackReadyAt, player.SkillReadyAt);
        }
    }

    /// <summary>참가자 정본과 호환 배열이 순서까지 같은 정확히 네 명인지 검사합니다.</summary>
    private static PlayerCombatSnapshot[] ValidateParticipants(GameRoomSnapshot snapshot)
    {
        var players = snapshot.Players;
        if (players is null || players.Length != GameRoomState.TargetPlayerCount
            || players.Select(player => player.PlayerId).Distinct().Count() != GameRoomState.TargetPlayerCount
            || !players.Select(player => player.PlayerId).SequenceEqual(snapshot.PlayerIds)
            || !players.Select(player => player.PlayerOrder).SequenceEqual(Enumerable.Range(0, 4)))
        {
            throw new InvalidOperationException("방 참가자는 배정 순서가 일치하는 네 명이어야 합니다.");
        }

        if (snapshot.CombatRuleVersion is not (0 or GameRoomState.CurrentCombatRuleVersion)
            || (snapshot.CombatRuleVersion == 0 && snapshot.Lifecycle != GameRoomLifecycle.Completed))
        {
            throw new InvalidOperationException("복원할 수 없는 전투 규칙 버전 또는 과거 방 상태입니다.");
        }

        return players;
    }

    /// <summary>
    /// 완료된 방에는 정확히 네 개의 Pending 결과가 존재하도록 만들고 기존 행의 불변 값을 검증합니다.
    /// </summary>
    private static async Task SynchronizeGameResultsAsync(
        GameDbContext gameDbContext,
        GameRoomSnapshot snapshot)
    {
        var existingResults = await gameDbContext.GameResults
            .Where(result => result.RoomId == snapshot.RoomId)
            .ToArrayAsync();

        if (snapshot.Lifecycle is not GameRoomLifecycle.Completed)
        {
            if (existingResults.Length > 0)
            {
                throw new InvalidOperationException(
                    $"완료되지 않은 게임 방 {snapshot.RoomId}에 결과 전달 행이 존재합니다.");
            }

            return;
        }

        var expectedPlayerIds = snapshot.PlayerIds.ToHashSet();
        if (existingResults.Any(result => !expectedPlayerIds.Contains(result.PlayerId)))
        {
            throw new InvalidOperationException(
                $"게임 방 {snapshot.RoomId}의 참가자가 아닌 결과 전달 행이 존재합니다.");
        }

        var updatedAt = snapshot.CompletedAt
            ?? throw new InvalidOperationException("완료된 게임 방에 완료 시각이 없습니다.");

        foreach (var playerId in snapshot.PlayerIds)
        {
            var rewardRequestId = CreateRewardRequestId(
                snapshot.RoomId,
                playerId,
                snapshot.RewardPolicyVersion);
            var existingResult = existingResults.SingleOrDefault(result => result.PlayerId == playerId);

            if (existingResult is null)
            {
                gameDbContext.GameResults.Add(new GameResultRecord(
                    snapshot.RoomId,
                    playerId,
                    snapshot.RewardPolicyVersion,
                    rewardRequestId,
                    updatedAt));
                continue;
            }

            if (existingResult.RewardPolicyVersion != snapshot.RewardPolicyVersion
                || existingResult.RewardRequestId != rewardRequestId)
            {
                // 이미 발급된 정책 버전이나 멱등성 키를 자동 수정하면 중복 보상 위험이 생깁니다.
                throw new InvalidOperationException(
                    $"게임 방 {snapshot.RoomId}, 플레이어 {playerId}의 보상 식별 정보가 방 상태와 다릅니다.");
            }
        }
    }

    /// <summary>
    /// 방·플레이어·정책 버전이 같으면 언제나 같은 보상 requestId를 만드는 결정적 함수입니다.
    /// </summary>
    private static Guid CreateRewardRequestId(
        Guid roomId,
        Guid playerId,
        int rewardPolicyVersion)
    {
        var source = string.Concat(
            "game-room-reward-v1:",
            roomId.ToString("D"),
            ":",
            playerId.ToString("D"),
            ":",
            rewardPolicyVersion.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));

        // bigEndian=true를 사용해야 해시 앞 16바이트의 표시 순서가 PostgreSQL Backfill UUID와 같습니다.
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>메모리의 최초 명령과 결과를 PostgreSQL JSON 행으로 변환합니다.</summary>
    private static GameRoomRequestRecord CreateRequestRecord(
        Guid roomId,
        GameRoomStoredRequest storedRequest)
    {
        var requestPayloadJson = storedRequest.CommandKind switch
        {
            GameRoomCommandKind.Create => JsonSerializer.Serialize(
                storedRequest.CreateAssignment
                ?? throw new InvalidOperationException("게임 방 생성 요청 기록이 없습니다."),
                JsonOptions),
            GameRoomCommandKind.Start => null,
            GameRoomCommandKind.Complete => JsonSerializer.Serialize(
                new CompleteGameRoomPayload(
                    storedRequest.CompleteOutcome
                    ?? throw new InvalidOperationException("게임 방 완료 결과 기록이 없습니다.")),
                JsonOptions),
            _ => throw new InvalidOperationException("알 수 없는 게임 방 명령 종류입니다."),
        };

        return new GameRoomRequestRecord(
            storedRequest.RequestId,
            roomId,
            storedRequest.CommandKind.ToString(),
            requestPayloadJson,
            JsonSerializer.Serialize(storedRequest.Result, JsonOptions),
            storedRequest.CreatedAt);
    }

    /// <summary>PostgreSQL JSON 행을 메모리 멱등성 기록으로 복원합니다.</summary>
    private static GameRoomStoredRequest RestoreStoredRequest(GameRoomRequestRecord record)
    {
        if (!Enum.TryParse<GameRoomCommandKind>(record.CommandKind, ignoreCase: false, out var commandKind))
        {
            throw new InvalidOperationException($"알 수 없는 게임 방 명령 종류입니다: {record.CommandKind}");
        }

        var result = JsonSerializer.Deserialize<GameRoomCommandResult>(record.ResultPayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("저장된 게임 방 명령 결과를 복원할 수 없습니다.");
        var assignment = commandKind == GameRoomCommandKind.Create
            ? JsonSerializer.Deserialize<MatchAssignment>(
                record.RequestPayloadJson
                ?? throw new InvalidOperationException("저장된 게임 방 생성 요청 본문이 없습니다."),
                JsonOptions)
            : null;
        GameOutcome? completeOutcome = commandKind == GameRoomCommandKind.Complete
            ? JsonSerializer.Deserialize<CompleteGameRoomPayload>(
                record.RequestPayloadJson
                ?? throw new InvalidOperationException("저장된 게임 방 완료 요청 본문이 없습니다."),
                JsonOptions)?.Outcome
                ?? throw new InvalidOperationException("저장된 게임 방 완료 결과를 복원할 수 없습니다.")
            : null;

        return new GameRoomStoredRequest(
            record.RequestId,
            commandKind,
            assignment,
            completeOutcome,
            result,
            record.CreatedAt);
    }

    /// <summary>PostgreSQL 행을 Orleans가 반환할 읽기 전용 방 스냅샷으로 바꿉니다.</summary>
    private static GameRoomSnapshot RestoreSnapshot(GameRoomRecord record, GameRoomPlayerRecord[] participants)
    {
        return new GameRoomSnapshot(
            record.RoomId,
            record.QueueKey,
            (GameRoomLifecycle)record.Lifecycle,
            record.PartyIds.ToArray(),
            record.PlayerIds.ToArray(),
            record.CreatedAt,
            record.StartedAt,
            record.CompletedAt,
            (GameOutcome)record.Outcome,
            record.RewardPolicyVersion,
            record.CombatRuleVersion,
            participants.Select(player => new PlayerCombatSnapshot(player.PlayerId, player.PlayerOrder,
                player.MaxHealth, player.CurrentHealth, (PlayerCombatStatus)player.CombatStatus,
                player.LastCommandSequence, player.BasicAttackReadyAt, player.SkillReadyAt)).ToArray(),
            record.CurrentWave, record.MaxWaves, record.EnemyMaxHealth,
            record.EnemyCurrentHealth, record.StateVersion, record.EnemyAttackSequence);
    }

    /// <summary>Complete 명령의 멱등성 비교를 위해 JSON에 저장하는 최소 요청 원문입니다.</summary>
    private sealed record CompleteGameRoomPayload(GameOutcome Outcome);
}
