using System.Security.Cryptography;
using System.Text.Json;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Matchmaking;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Grains.Matchmaking;

/// <summary>권한을 확인한 매칭 의도를 먼저 저장하고, 같은 하위 요청 ID로 완료 또는 보상 복구합니다.</summary>
public sealed class MatchmakingOperationGrain(
    IDbContextFactory<GameDbContext> dbContextFactory, TimeProvider timeProvider)
    : Grain, IMatchmakingOperationGrain
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MatchmakingOperationResult> ExecuteAsync(MatchmakingOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.TargetId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.QueueKey) || request.QueueKey.Length > 100
            || !Enum.IsDefined(request.Kind) || request.GetGrainKey() != this.GetPrimaryKeyString())
            throw new ArgumentException("유효한 매칭 작업 키와 입력이 필요합니다.", nameof(request));

        await using var context = await dbContextFactory.CreateDbContextAsync();
        var record = await context.MatchmakingOperations.FindAsync(this.GetPrimaryKeyString());
        if (record is not null)
        {
            if (ReadRequest(record) != request) return QueueFailure(MatchQueueCommandError.RequestIdConflict);
            if (record.ResultPayloadJson is not null) return Replay(ReadResult(record));
        }
        else
        {
            // 권한 거부는 다른 Grain을 변경하지 않고 종료합니다. 승인한 리더·파티는 의도와 함께 고정합니다.
            Guid leader;
            Guid? partyId;
            if (request.Kind == MatchmakingOperationKind.EnqueueParty)
            {
                var party = await GrainFactory.GetGrain<IPartyGrain>(request.TargetId).GetAsync();
                if (party is null) return Failure(MatchmakingOperationError.PartyNotFound);
                if (party.LeaderPlayerId is not Guid partyLeader)
                    return Failure(MatchmakingOperationError.PartyTransitionFailed, PartyCommandError.PartyDisbanded);
                if (!request.IsAdministrator && partyLeader != request.RequesterPlayerId)
                    return Failure(MatchmakingOperationError.RequesterIsNotPartyLeader);
                leader = partyLeader;
                partyId = request.TargetId;
            }
            else
            {
                var ticket = await GrainFactory.GetGrain<IMatchQueueGrain>(request.QueueKey).GetTicketAsync(request.TargetId);
                if (ticket is null) return QueueFailure(MatchQueueCommandError.TicketNotFound);
                if (!request.IsAdministrator && ticket.LeaderPlayerId != request.RequesterPlayerId)
                    return Failure(MatchmakingOperationError.RequesterCannotManageTicket);
                leader = ticket.LeaderPlayerId;
                partyId = ticket.PartyId;
            }

            record = new MatchmakingOperationRecord(this.GetPrimaryKeyString(),
                JsonSerializer.Serialize(request, JsonOptions), leader, partyId, timeProvider.GetUtcNow());
            context.MatchmakingOperations.Add(record);
            // 이 저장이 성공한 뒤부터는 호출자가 사라져도 복구 작업자가 책임지고 실행합니다.
            await context.SaveChangesAsync();
        }

        return await RunAsync(context, record, request);
    }

    public async Task ResumeAsync()
    {
        await using var context = await dbContextFactory.CreateDbContextAsync();
        var record = await context.MatchmakingOperations.FindAsync(this.GetPrimaryKeyString());
        if (record is null || record.ResultPayloadJson is not null || record.NextAttemptAt > timeProvider.GetUtcNow()) return;
        await RunAsync(context, record, ReadRequest(record));
    }

    private async Task<MatchmakingOperationResult> RunAsync(GameDbContext context,
        MatchmakingOperationRecord record, MatchmakingOperationRequest request)
    {
        try
        {
            var result = request.Kind == MatchmakingOperationKind.EnqueueParty
                ? await EnqueuePartyAsync(record, request)
                : await CancelAsync(record, request);
            record.Complete(JsonSerializer.Serialize(result, JsonOptions));
            await context.SaveChangesAsync();
            return result;
        }
        catch
        {
            // 완료 응답 저장까지 실패해도 최초 의도는 남습니다. 재개는 기존 Grain 영수증을 재생합니다.
            // 실패 기록 저장 자체가 실패하더라도 최초 미완료 행은 다음 조회 대상입니다.
            await context.Entry(record).ReloadAsync();
            record.ScheduleRetry(timeProvider.GetUtcNow().AddSeconds(5));
            await context.SaveChangesAsync();
            throw;
        }
    }

    private async Task<MatchmakingOperationResult> EnqueuePartyAsync(
        MatchmakingOperationRecord record, MatchmakingOperationRequest request)
    {
        var partyId = record.PartyId!.Value;
        var party = GrainFactory.GetGrain<IPartyGrain>(partyId);
        var locked = await party.QueueForMatchAsync(ChildId(request.RequestId, partyId, 1), record.LeaderPlayerId);
        if (locked.Error != PartyCommandError.None)
            return Failure(MatchmakingOperationError.PartyTransitionFailed, locked.Error);

        var queueResult = await GrainFactory.GetGrain<IMatchQueueGrain>(request.QueueKey).EnqueueAsync(
            new MatchQueueEntryRequest(request.RequestId, MatchQueueEntryKind.PreformedParty, partyId,
                record.LeaderPlayerId, locked.Party!.MemberPlayerIds.ToArray()));
        if (queueResult.Error != MatchQueueCommandError.None)
        {
            // 명시적인 업무 거부만 보상 복구합니다. 통신 예외는 수락됐을 수 있으므로 같은 요청으로 재개합니다.
            var unlocked = await party.CancelMatchQueueAsync(ChildId(request.RequestId, partyId, 2), record.LeaderPlayerId);
            if (unlocked.Error != PartyCommandError.None)
                return Failure(MatchmakingOperationError.PartyCompensationFailed, unlocked.Error);
        }
        return Success(queueResult);
    }

    private async Task<MatchmakingOperationResult> CancelAsync(
        MatchmakingOperationRecord record, MatchmakingOperationRequest request)
    {
        var result = await GrainFactory.GetGrain<IMatchQueueGrain>(request.QueueKey).CancelAsync(
            new CancelMatchQueueRequest(request.RequestId, request.TargetId, record.LeaderPlayerId));
        if (result.Error == MatchQueueCommandError.None && record.PartyId is Guid partyId)
        {
            var unlocked = await GrainFactory.GetGrain<IPartyGrain>(partyId).CancelMatchQueueAsync(
                ChildId(request.RequestId, partyId, 3), record.LeaderPlayerId);
            if (unlocked.Error != PartyCommandError.None)
                return Failure(MatchmakingOperationError.PartyTransitionFailed, unlocked.Error);
        }
        return Success(result);
    }

    /// <summary>기존 API가 사용한 SHA-256 입력 순서를 유지하여 배포 전 하위 요청도 재생합니다.</summary>
    private static Guid ChildId(Guid requestId, Guid partyId, byte marker)
    {
        Span<byte> source = stackalloc byte[33];
        requestId.TryWriteBytes(source[..16]);
        partyId.TryWriteBytes(source.Slice(16, 16));
        source[32] = marker;
        return new Guid(SHA256.HashData(source).AsSpan(0, 16));
    }

    private static MatchmakingOperationRequest ReadRequest(MatchmakingOperationRecord record) =>
        JsonSerializer.Deserialize<MatchmakingOperationRequest>(record.RequestPayloadJson, JsonOptions)
        ?? throw new InvalidOperationException("저장된 매칭 작업 입력이 없습니다.");

    private static MatchmakingOperationResult ReadResult(MatchmakingOperationRecord record) =>
        JsonSerializer.Deserialize<MatchmakingOperationResult>(record.ResultPayloadJson!, JsonOptions)
        ?? throw new InvalidOperationException("저장된 매칭 작업 결과가 없습니다.");

    private static MatchmakingOperationResult Replay(MatchmakingOperationResult result) =>
        result with { QueueResult = result.QueueResult is { } queue ? queue with { IsReplay = true } : null };

    private static MatchmakingOperationResult Success(MatchQueueCommandResult result) => new(MatchmakingOperationError.None, null, result);
    private static MatchmakingOperationResult Failure(MatchmakingOperationError error, PartyCommandError? partyError = null) => new(error, partyError, null);
    private static MatchmakingOperationResult QueueFailure(MatchQueueCommandError error) => Success(new(false, error, null, null));
}
