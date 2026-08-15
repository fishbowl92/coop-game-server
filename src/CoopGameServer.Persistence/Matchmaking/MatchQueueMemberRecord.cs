namespace CoopGameServer.Persistence.Matchmaking;

/// <summary>PostgreSQL의 match_queue_members 테이블에 저장되는 티켓별 참가자와 순서입니다.</summary>
public sealed class MatchQueueMemberRecord
{
    private MatchQueueMemberRecord()
    {
    }

    /// <summary>대기 티켓의 한 멤버 저장 행을 만듭니다.</summary>
    public MatchQueueMemberRecord(Guid ticketId, Guid playerId, int memberOrder)
    {
        TicketId = ticketId;
        PlayerId = playerId;
        MemberOrder = memberOrder;
    }

    /// <summary>소속 대기 티켓 ID입니다.</summary>
    public Guid TicketId { get; private set; }

    /// <summary>티켓에 포함된 플레이어 ID입니다.</summary>
    public Guid PlayerId { get; private set; }

    /// <summary>파티 멤버 순서를 보존하는 0부터 시작하는 값입니다.</summary>
    public int MemberOrder { get; private set; }
}
