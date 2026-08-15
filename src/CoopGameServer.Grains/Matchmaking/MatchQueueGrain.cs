using System.Text.Json;
using System.Text.Json.Serialization;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Matchmaking;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Grains.Matchmaking;

/// <summary>
/// 한 매칭 조건의 대기열 명령을 순차 처리하고 PostgreSQL에 영속화하는 Orleans Grain 구현체입니다.
/// </summary>
/// <remarks>
/// 같은 Grain에는 한 번에 하나의 요청만 실행되는 Orleans의 기본 실행 규칙이 적용됩니다.
/// 명령마다 PostgreSQL 트랜잭션에 저장하므로 Silo가 재시작되어도 대기 순서와 멱등성 결과를 복원합니다.
/// </remarks>
public sealed class MatchQueueGrain(IDbContextFactory<GameDbContext> dbContextFactory) : Grain, IMatchQueueGrain
{
    // 요청·응답 JSON을 DB에서 사람이 읽기 쉽게 남기기 위해 열거형을 숫자 대신 이름으로 기록합니다.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private MatchQueueState _state = new();

    /// <summary>Grain 활성화 시 PostgreSQL에서 이 매칭 조건의 대기열과 요청 이력을 복원합니다.</summary>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var queueKey = this.GetPrimaryKeyString();
        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var ticketRecords = await gameDbContext.MatchQueueTickets
            .AsNoTracking()
            .Where(ticket => ticket.QueueKey == queueKey)
            .OrderBy(ticket => ticket.QueueOrder)
            .ToArrayAsync(cancellationToken);
        var ticketIds = ticketRecords.Select(ticket => ticket.TicketId).ToArray();
        var memberRecords = await gameDbContext.MatchQueueMembers
            .AsNoTracking()
            .Where(member => ticketIds.Contains(member.TicketId))
            .OrderBy(member => member.MemberOrder)
            .ToArrayAsync(cancellationToken);
        var membersByTicketId = memberRecords
            .GroupBy(member => member.TicketId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(member => member.PlayerId).ToArray());
        var requestRecords = await gameDbContext.MatchQueueRequests
            .AsNoTracking()
            .Where(request => request.QueueKey == queueKey)
            .OrderBy(request => request.CreatedAt)
            .ToArrayAsync(cancellationToken);

        _state = MatchQueueState.Restore(
            ticketRecords.Select(ticket => new MatchQueueTicket(
                ticket.TicketId,
                ticket.QueueKey,
                (MatchQueueEntryKind)ticket.EntryKind,
                ticket.PartyId,
                ticket.LeaderPlayerId,
                membersByTicketId.GetValueOrDefault(ticket.TicketId, []),
                (MatchQueueTicketStatus)ticket.Status,
                ticket.RoomId,
                ticket.EnqueuedAt,
                ticket.QueueOrder)),
            requestRecords.Select(RestoreStoredRequest));

        await base.OnActivateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<MatchQueueCommandResult> EnqueueAsync(MatchQueueEntryRequest request)
    {
        return ExecuteCommandAsync(state => state.Enqueue(this.GetPrimaryKeyString(), request));
    }

    /// <inheritdoc />
    public Task<MatchQueueCommandResult> CancelAsync(CancelMatchQueueRequest request)
    {
        return ExecuteCommandAsync(state => state.Cancel(request));
    }

    /// <inheritdoc />
    public Task<MatchQueueTicket?> GetTicketAsync(Guid ticketId)
    {
        return Task.FromResult(_state.GetTicket(ticketId));
    }

    /// <inheritdoc />
    public Task<MatchQueueSnapshot> GetSnapshotAsync()
    {
        return Task.FromResult(_state.GetSnapshot(this.GetPrimaryKeyString()));
    }

    /// <summary>
    /// 복사한 상태에 명령을 적용하고, DB 커밋이 성공했을 때만 실제 Grain 상태를 교체합니다.
    /// </summary>
    private async Task<MatchQueueCommandResult> ExecuteCommandAsync(
        Func<MatchQueueState, MatchQueueCommandResult> executeCommand)
    {
        var candidateState = _state.Clone();
        var result = executeCommand(candidateState);

        await using var gameDbContext = await dbContextFactory.CreateDbContextAsync();
        await using var transaction = await gameDbContext.Database.BeginTransactionAsync();

        // 같은 queueKey Grain은 Orleans가 순차 실행하지만, DB에도 한 번에 완성된 상태만 보이도록 트랜잭션을 사용합니다.
        await SynchronizeStateAsync(gameDbContext, this.GetPrimaryKeyString(), candidateState);
        await gameDbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        _state = candidateState;
        return result;
    }

    /// <summary>
    /// 해당 queueKey의 이전 저장 상태를 트랜잭션 안에서 교체하고 후보 상태 전체를 다시 기록합니다.
    /// </summary>
    /// <remarks>
    /// 현재 프로젝트의 대기열은 최대 4명 조합 학습용 범위입니다.
    /// 이후 대규모 운영 단계에서는 변경된 티켓만 갱신하는 방식으로 최적화할 수 있습니다.
    /// </remarks>
    private static async Task SynchronizeStateAsync(
        GameDbContext gameDbContext,
        string queueKey,
        MatchQueueState state)
    {
        // ExecuteDeleteAsync는 EF 추적 객체를 만들지 않고 즉시 DELETE SQL을 실행합니다.
        // 멤버 → 티켓 → 요청 순서로 비워 외래 키 제약을 지킵니다.
        var existingTicketIds = gameDbContext.MatchQueueTickets
            .Where(ticket => ticket.QueueKey == queueKey)
            .Select(ticket => ticket.TicketId);
        await gameDbContext.MatchQueueMembers
            .Where(member => existingTicketIds.Contains(member.TicketId))
            .ExecuteDeleteAsync();
        await gameDbContext.MatchQueueTickets
            .Where(ticket => ticket.QueueKey == queueKey)
            .ExecuteDeleteAsync();
        await gameDbContext.MatchQueueRequests
            .Where(request => request.QueueKey == queueKey)
            .ExecuteDeleteAsync();

        foreach (var ticket in state.GetTickets())
        {
            gameDbContext.MatchQueueTickets.Add(new MatchQueueTicketRecord(
                ticket.TicketId,
                ticket.QueueKey,
                (int)ticket.EntryKind,
                ticket.PartyId,
                ticket.LeaderPlayerId,
                (int)ticket.Status,
                ticket.RoomId,
                ticket.EnqueuedAt,
                ticket.QueueOrder));

            foreach (var member in ticket.MemberPlayerIds.Select((playerId, memberOrder) => new { playerId, memberOrder }))
            {
                gameDbContext.MatchQueueMembers.Add(new MatchQueueMemberRecord(
                    ticket.TicketId,
                    member.playerId,
                    member.memberOrder));
            }
        }

        foreach (var storedRequest in state.GetStoredRequests())
        {
            gameDbContext.MatchQueueRequests.Add(CreateRequestRecord(queueKey, storedRequest));
        }
    }

    /// <summary>메모리의 최초 요청·응답을 재시작 복원용 JSON 행으로 변환합니다.</summary>
    private static MatchQueueRequestRecord CreateRequestRecord(
        string queueKey,
        MatchQueueStoredRequest storedRequest)
    {
        var requestPayload = storedRequest.CommandKind switch
        {
            MatchQueueCommandKind.Enqueue => JsonSerializer.Serialize(
                storedRequest.EnqueueRequest
                ?? throw new InvalidOperationException("등록 요청 기록이 없습니다."),
                JsonOptions),
            MatchQueueCommandKind.Cancel => JsonSerializer.Serialize(
                storedRequest.CancelRequest
                ?? throw new InvalidOperationException("취소 요청 기록이 없습니다."),
                JsonOptions),
            _ => throw new InvalidOperationException("알 수 없는 대기열 명령 종류입니다."),
        };

        return new MatchQueueRequestRecord(
            storedRequest.RequestId,
            queueKey,
            storedRequest.CommandKind.ToString(),
            requestPayload,
            JsonSerializer.Serialize(storedRequest.Result, JsonOptions),
            storedRequest.CreatedAt);
    }

    /// <summary>DB JSON 행을 메모리 멱등성 기록으로 복원합니다.</summary>
    private static MatchQueueStoredRequest RestoreStoredRequest(MatchQueueRequestRecord record)
    {
        if (!Enum.TryParse<MatchQueueCommandKind>(record.CommandKind, ignoreCase: false, out var commandKind))
        {
            throw new InvalidOperationException($"알 수 없는 대기열 명령 종류입니다: {record.CommandKind}");
        }

        var result = JsonSerializer.Deserialize<MatchQueueCommandResult>(record.ResultPayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("저장된 대기열 명령 결과를 복원할 수 없습니다.");

        return commandKind switch
        {
            MatchQueueCommandKind.Enqueue => new MatchQueueStoredRequest(
                record.RequestId,
                commandKind,
                JsonSerializer.Deserialize<MatchQueueEntryRequest>(record.RequestPayloadJson, JsonOptions)
                    ?? throw new InvalidOperationException("저장된 대기열 등록 요청을 복원할 수 없습니다."),
                CancelRequest: null,
                result,
                record.CreatedAt),
            MatchQueueCommandKind.Cancel => new MatchQueueStoredRequest(
                record.RequestId,
                commandKind,
                EnqueueRequest: null,
                JsonSerializer.Deserialize<CancelMatchQueueRequest>(record.RequestPayloadJson, JsonOptions)
                    ?? throw new InvalidOperationException("저장된 대기열 취소 요청을 복원할 수 없습니다."),
                result,
                record.CreatedAt),
            _ => throw new InvalidOperationException("알 수 없는 대기열 명령 종류입니다."),
        };
    }
}
