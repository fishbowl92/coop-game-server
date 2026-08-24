namespace CoopGameServer.GrainContracts.Players;

/// <summary>플레이어 진행도 페이지 조회를 거부한 업무상 이유입니다.</summary>
[GenerateSerializer]
public enum PlayerProgressionQueryError
{
    /// <summary>조회 오류가 없습니다.</summary>
    None = 0,

    /// <summary>페이지 크기가 허용 범위인 1~100을 벗어났습니다.</summary>
    InvalidPageSize = 1,

    /// <summary>연속 토큰의 버전 또는 형식이 올바르지 않습니다.</summary>
    InvalidContinuationToken = 2,

    /// <summary>대상 Player가 PostgreSQL에 존재하지 않습니다.</summary>
    PlayerNotFound = 3,
}
