namespace CoopGameServer.GrainContracts.Parties;

/// <summary>
/// 한 번 생성된 파티의 수명 주기 상태입니다.
/// </summary>
public enum PartyLifecycle
{
    /// <summary>멤버를 받고 게임 기능을 수행할 수 있는 상태입니다.</summary>
    Active,

    /// <summary>더 이상 가입하거나 같은 partyId로 다시 생성할 수 없는 상태입니다.</summary>
    Disbanded,
}
