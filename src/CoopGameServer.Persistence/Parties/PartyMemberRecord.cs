namespace CoopGameServer.Persistence.Parties;

/// <summary>
/// PostgreSQL의 party_members 테이블에 저장되는 파티 멤버 한 명의 정보입니다.
/// </summary>
public sealed class PartyMemberRecord
{
    private PartyMemberRecord()
    {
    }

    /// <summary>
    /// 파티 멤버 저장 행을 만듭니다.
    /// </summary>
    public PartyMemberRecord(Guid partyId, Guid playerId, int joinOrder)
    {
        PartyId = partyId;
        PlayerId = playerId;
        JoinOrder = joinOrder;
    }

    /// <summary>멤버가 속한 파티의 식별자입니다.</summary>
    public Guid PartyId { get; private set; }

    /// <summary>파티에 가입한 플레이어의 식별자입니다.</summary>
    public Guid PlayerId { get; private set; }

    /// <summary>0부터 시작하는 가입 순서이며 리더 승계 순서를 결정할 때 사용합니다.</summary>
    public int JoinOrder { get; private set; }

    /// <summary>
    /// 멤버 탈퇴 뒤 남은 멤버들의 연속적인 가입 순서를 반영합니다.
    /// </summary>
    public void UpdateJoinOrder(int joinOrder)
    {
        JoinOrder = joinOrder;
    }
}
