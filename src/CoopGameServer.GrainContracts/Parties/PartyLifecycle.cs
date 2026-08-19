namespace CoopGameServer.GrainContracts.Parties;

/// <summary>
/// 한 번 생성된 파티의 수명 주기 상태입니다.
/// </summary>
public enum PartyLifecycle
{
    /// <summary>멤버를 받고 게임 기능을 수행할 수 있는 상태입니다.</summary>
    Active = 0,

    /// <summary>더 이상 가입하거나 같은 partyId로 다시 생성할 수 없는 종료 상태입니다.</summary>
    /// <remarks>
    /// 기존 PostgreSQL 행이 1을 해산 상태로 저장하고 있으므로 이 번호는 변경하지 않습니다.
    /// </remarks>
    Disbanded = 1,

    /// <summary>매칭 대기열에 등록되어 멤버 구성이 잠긴 상태입니다.</summary>
    MatchQueued = 2,

    /// <summary>매칭된 게임 방에 참가 중이어서 멤버 구성이 잠긴 상태입니다.</summary>
    InGame = 3,
}
