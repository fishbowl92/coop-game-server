namespace CoopGameServer.Domain.Administration;

/// <summary>관리자 감사 기록이 나타내는 운영 작업 종류입니다.</summary>
public enum AdminAuditAction
{
    /// <summary>정의되지 않은 값입니다. 저장할 수 없습니다.</summary>
    None = 0,

    /// <summary>관리자가 한 Player에게 골드 또는 아이템을 지급한 작업입니다.</summary>
    GrantReward = 1,
}
