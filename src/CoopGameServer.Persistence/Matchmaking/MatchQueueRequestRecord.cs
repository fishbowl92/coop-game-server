namespace CoopGameServer.Persistence.Matchmaking;

/// <summary>PostgreSQL의 match_queue_requests 테이블에 저장되는 대기열 명령의 최초 입력과 응답입니다.</summary>
/// <remarks>
/// 명령 본문·결과를 JSON으로 보관하면 Silo 재시작 후에도 당시의 티켓 상태와 매칭 결과를 그대로 재생할 수 있습니다.
/// 상태 조회·조합에는 JSON이 아니라 match_queue_tickets와 match_queue_members를 사용합니다.
/// </remarks>
public sealed class MatchQueueRequestRecord
{
    private MatchQueueRequestRecord()
    {
    }

    /// <summary>대기열 명령 기록 행을 만듭니다.</summary>
    public MatchQueueRequestRecord(
        Guid requestId,
        string queueKey,
        string commandKind,
        string requestPayloadJson,
        string resultPayloadJson,
        DateTimeOffset createdAt)
    {
        RequestId = requestId;
        QueueKey = queueKey;
        CommandKind = commandKind;
        RequestPayloadJson = requestPayloadJson;
        ResultPayloadJson = resultPayloadJson;
        CreatedAt = createdAt;
    }

    /// <summary>같은 명령의 재시도를 식별하는 전역 멱등성 키입니다.</summary>
    public Guid RequestId { get; private set; }

    /// <summary>이 요청을 처리한 MatchQueueGrain의 문자열 키입니다.</summary>
    public string QueueKey { get; private set; } = string.Empty;

    /// <summary>Enqueue 또는 Cancel인 명령 종류입니다.</summary>
    public string CommandKind { get; private set; } = string.Empty;

    /// <summary>최초 요청 본문을 직렬화한 JSON 문자열입니다.</summary>
    public string RequestPayloadJson { get; private set; } = string.Empty;

    /// <summary>최초 응답 전체를 직렬화한 JSON 문자열입니다.</summary>
    public string ResultPayloadJson { get; private set; } = string.Empty;

    /// <summary>명령을 처음 처리한 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
