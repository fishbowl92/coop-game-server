namespace CoopGameServer.Persistence.Matchmaking;

/// <summary>파티 잠금과 티켓 변경 사이에 장애가 발생해도 원래 입력으로 재개할 수 있는 작업 기록입니다.</summary>
public sealed class MatchmakingOperationRecord
{
    private MatchmakingOperationRecord() { }

    public MatchmakingOperationRecord(string operationKey, string requestPayloadJson,
        Guid leaderPlayerId, Guid? partyId, DateTimeOffset now)
    {
        OperationKey = operationKey;
        RequestPayloadJson = requestPayloadJson;
        LeaderPlayerId = leaderPlayerId;
        PartyId = partyId;
        CreatedAt = now;
        NextAttemptAt = now;
    }

    public string OperationKey { get; private set; } = string.Empty;
    public string RequestPayloadJson { get; private set; } = string.Empty;
    public Guid LeaderPlayerId { get; private set; }
    public Guid? PartyId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string? ResultPayloadJson { get; private set; }

    /// <summary>완료 결과도 보존하여 같은 요청이 나중의 파티 상태를 다시 변경하지 않게 합니다.</summary>
    public void Complete(string resultPayloadJson) => ResultPayloadJson = resultPayloadJson;

    /// <summary>실패한 작업을 잠시 뒤로 보내 다른 미완료 작업의 복구 기회를 보장합니다.</summary>
    public void ScheduleRetry(DateTimeOffset nextAttemptAt) => NextAttemptAt = nextAttemptAt;
}
