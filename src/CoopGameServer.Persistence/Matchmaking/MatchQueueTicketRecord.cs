namespace CoopGameServer.Persistence.Matchmaking;

/// <summary>PostgreSQL의 match_queue_tickets 테이블에 저장되는 매칭 대기 티켓입니다.</summary>
/// <remarks>
/// 사전 구성 파티면 PartyId가 있고, 솔로 참가자면 PartyId는 null입니다.
/// 멤버 목록은 match_queue_members 테이블로 분리하여 순서와 개별 플레이어를 보존합니다.
/// </remarks>
public sealed class MatchQueueTicketRecord
{
    private MatchQueueTicketRecord()
    {
    }

    /// <summary>새 대기 티켓 저장 행을 만듭니다.</summary>
    public MatchQueueTicketRecord(
        Guid ticketId,
        string queueKey,
        int entryKind,
        Guid? partyId,
        Guid leaderPlayerId,
        int status,
        Guid? roomId,
        DateTimeOffset enqueuedAt,
        long queueOrder)
    {
        TicketId = ticketId;
        QueueKey = queueKey;
        EntryKind = entryKind;
        PartyId = partyId;
        LeaderPlayerId = leaderPlayerId;
        Status = status;
        RoomId = roomId;
        EnqueuedAt = enqueuedAt;
        QueueOrder = queueOrder;
    }

    /// <summary>매칭 대기 티켓의 고유 식별자입니다.</summary>
    public Guid TicketId { get; private set; }

    /// <summary>게임 모드·난이도 등 대기열을 구분하는 Orleans 문자열 키입니다.</summary>
    public string QueueKey { get; private set; } = string.Empty;

    /// <summary>MatchQueueEntryKind 열거형 값을 정수로 저장한 참가 유형입니다.</summary>
    public int EntryKind { get; private set; }

    /// <summary>사전 구성 파티의 ID이며 솔로 참가자는 null입니다.</summary>
    public Guid? PartyId { get; private set; }

    /// <summary>대기 취소 권한을 가진 파티 리더 또는 솔로 플레이어입니다.</summary>
    public Guid LeaderPlayerId { get; private set; }

    /// <summary>MatchQueueTicketStatus 열거형 값을 정수로 저장한 현재 상태입니다.</summary>
    public int Status { get; private set; }

    /// <summary>4명이 매칭된 후 배정된 임시 게임 방 ID이며 대기 중이면 null입니다.</summary>
    public Guid? RoomId { get; private set; }

    /// <summary>대기열에 최초 등록된 UTC 시각입니다.</summary>
    public DateTimeOffset EnqueuedAt { get; private set; }

    /// <summary>같은 대기열 안에서 먼저 등록된 티켓을 결정적으로 구분하는 증가 순번입니다.</summary>
    public long QueueOrder { get; private set; }
}
