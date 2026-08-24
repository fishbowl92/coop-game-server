namespace CoopGameServer.GrainContracts.Players;

/// <summary>PlayerGrain 보상 명령의 업무 처리 상태입니다.</summary>
[GenerateSerializer]
public enum PlayerRewardCommandStatus
{
    /// <summary>보상이 새로 적용됐거나 이미 적용된 같은 결과를 재생했습니다.</summary>
    Applied = 0,

    /// <summary>서버 정책상 지급할 보상이 없어 정상적으로 지급 단계를 생략했습니다.</summary>
    NoReward = 1,

    /// <summary>입력 또는 업무 규칙 위반으로 명령을 거부했습니다.</summary>
    Rejected = 2,
}
