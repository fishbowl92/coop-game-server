using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
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
public sealed class GameRoomGrain(IDbContextFactory<GameDbContext> dbContextFactory)
    : Grain, IGameRoomGrain
{
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

        _state = GameRoomState.Restore(
            roomRecord is null ? null : RestoreSnapshot(roomRecord),
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

        await PersistCandidateStateAsync(candidateState);
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
        var result = candidateState.Start(requestId, DateTimeOffset.UtcNow);

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

        await PersistCandidateStateAsync(candidateState);
        return result;
    }

    /// <inheritdoc />
    public async Task<GameRoomCommandResult> CompleteAsync(Guid requestId)
    {
        var currentRoom = _state.Get();
        var candidateState = _state.Clone();
        var result = candidateState.Complete(requestId, DateTimeOffset.UtcNow);

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

        await PersistCandidateStateAsync(candidateState);

        if (result.Error is GameRoomCommandError.None)
        {
            // 방 완료가 PostgreSQL에 확정된 뒤에만 참가자를 현재 매칭에서 해제합니다.
            // 반대 순서라면 방은 아직 InGame인데 같은 플레이어가 새 방에 들어갈 수 있습니다.
            await EnsureMatchTicketsCompletedAsync(
                candidateState.Get() ?? throw new InvalidOperationException("완료된 게임 방 상태가 없습니다."),
                requestId);
        }

        return result;
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

    /// <summary>후보 방 상태와 요청 결과를 하나의 PostgreSQL 트랜잭션으로 저장합니다.</summary>
    private async Task PersistCandidateStateAsync(GameRoomState candidateState)
    {
        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync();
        await using var transaction = await gameDbContext.Database.BeginTransactionAsync();

        await SynchronizeStateAsync(gameDbContext, this.GetPrimaryKey(), candidateState);
        await gameDbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        _state = candidateState;
    }

    /// <summary>현재 roomId의 방 한 행과 전체 멱등성 요청 기록을 후보 상태로 맞춥니다.</summary>
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
                snapshot.CompletedAt));
        }
        else
        {
            existingRoom.Update(
                (int)snapshot.Lifecycle,
                snapshot.PartyIds,
                snapshot.PlayerIds,
                snapshot.StartedAt,
                snapshot.CompletedAt);
        }

        await gameDbContext.GameRoomRequests
            .Where(request => request.RoomId == roomId)
            .ExecuteDeleteAsync();

        foreach (var storedRequest in state.GetStoredRequests())
        {
            gameDbContext.GameRoomRequests.Add(CreateRequestRecord(roomId, storedRequest));
        }
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
            GameRoomCommandKind.Start or GameRoomCommandKind.Complete => null,
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

        return new GameRoomStoredRequest(
            record.RequestId,
            commandKind,
            assignment,
            result,
            record.CreatedAt);
    }

    /// <summary>PostgreSQL 행을 Orleans가 반환할 읽기 전용 방 스냅샷으로 바꿉니다.</summary>
    private static GameRoomSnapshot RestoreSnapshot(GameRoomRecord record)
    {
        return new GameRoomSnapshot(
            record.RoomId,
            record.QueueKey,
            (GameRoomLifecycle)record.Lifecycle,
            record.PartyIds.ToArray(),
            record.PlayerIds.ToArray(),
            record.CreatedAt,
            record.StartedAt,
            record.CompletedAt);
    }
}
