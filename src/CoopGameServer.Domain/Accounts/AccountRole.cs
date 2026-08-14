namespace CoopGameServer.Domain.Accounts;

/// <summary>
/// 계정이 서버에서 가질 수 있는 역할을 구분합니다.
/// </summary>
/// <remarks>
/// Role(역할)은 "누가 어떤 API를 실행할 수 있는가"를 구분하는 인가(Authorization) 기준입니다.
/// 일반 플레이어는 자신의 게임 데이터만 다루고, 관리자는 운영 도구에서만 필요한 제한된 작업을 수행합니다.
/// </remarks>
public enum AccountRole
{
    /// <summary>일반 게임 플레이어 계정입니다.</summary>
    Player = 0,

    /// <summary>운영 전용 기능에 접근할 수 있는 관리자 계정입니다.</summary>
    Administrator = 1,
}
