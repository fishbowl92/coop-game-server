namespace CoopGameServer.Domain.Administration;

/// <summary>PostgreSQL에 최종 확정된 관리자 작업 결과입니다.</summary>
public enum AdminAuditResult
{
    /// <summary>정의되지 않은 값입니다. 저장할 수 없습니다.</summary>
    None = 0,

    /// <summary>관리자 작업과 대상 데이터 변경이 같은 Transaction에서 적용됐습니다.</summary>
    Applied = 1,
}
