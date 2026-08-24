namespace CoopGameServer.Persistence.Rewards;

/// <summary>보상 쓰기에서 예상 가능한 업무상 실패 이유입니다.</summary>
public enum RewardWriteError
{
    /// <summary>보상이 새로 적용됐거나 같은 기존 결과를 정상적으로 찾았습니다.</summary>
    None = 0,

    /// <summary>보상을 받을 Player 행이 존재하지 않습니다.</summary>
    PlayerNotFound = 1,

    /// <summary>같은 멱등성 키가 이전과 다른 보상 내용으로 재사용됐습니다.</summary>
    IdempotencyConflict = 2,
}
