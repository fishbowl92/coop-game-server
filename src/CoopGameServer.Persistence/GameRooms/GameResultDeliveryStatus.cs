namespace CoopGameServer.Persistence.GameRooms;

/// <summary>완료된 게임 결과를 PlayerGrain에 전달한 진행 상태입니다.</summary>
public enum GameResultDeliveryStatus
{
    /// <summary>아직 첫 전달을 시작하지 않았습니다.</summary>
    Pending = 0,

    /// <summary>일시적인 장애가 발생해 다음 재시도 시각을 기다립니다.</summary>
    PendingRetry = 1,

    /// <summary>실제 보상이 새로 적용됐거나 기존 적용 결과를 확인했습니다.</summary>
    Applied = 2,

    /// <summary>정책상 지급할 보상이 없음을 정상적으로 확정했습니다.</summary>
    NoReward = 3,

    /// <summary>자동 재시도로 해결할 수 없는 영구 오류가 발생했습니다.</summary>
    TerminalFailure = 4,
}
