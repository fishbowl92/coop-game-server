namespace CoopGameServer.GrainContracts.Players;

/// <summary>PlayerGrain 보상 명령이 거부된 구체적인 업무상 이유입니다.</summary>
[GenerateSerializer]
public enum PlayerRewardCommandError
{
    /// <summary>업무상 오류가 없습니다.</summary>
    None = 0,

    /// <summary>멱등성 키, 보상 수량 또는 사유의 형식이 올바르지 않습니다.</summary>
    InvalidRequest = 1,

    /// <summary>대상 Player가 PostgreSQL에 존재하지 않습니다.</summary>
    PlayerNotFound = 2,

    /// <summary>요청한 보상 정책 버전을 서버가 더 이상 지원하지 않습니다.</summary>
    UnsupportedRewardPolicy = 3,

    /// <summary>같은 멱등성 키가 이전과 다른 내용이나 실행 주체로 재사용됐습니다.</summary>
    IdempotencyConflict = 4,

    /// <summary>지급을 요청한 Account가 현재 Administrator 역할이 아닙니다.</summary>
    InvalidAdministrator = 5,
}
