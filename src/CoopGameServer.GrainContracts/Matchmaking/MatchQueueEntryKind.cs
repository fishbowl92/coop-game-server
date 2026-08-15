namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>매칭 대기열에 들어온 인원이 사전 구성 파티인지 솔로 참가자인지 나타냅니다.</summary>
[GenerateSerializer]
public enum MatchQueueEntryKind
{
    /// <summary>
    /// 실제 PartyGrain으로 관리되는 사전 구성 파티입니다.
    /// 게임이 끝나도 파티 자체는 해산하지 않고 로비에서 유지하는 대상입니다.
    /// </summary>
    PreformedParty = 0,

    /// <summary>
    /// 특정 파티에 속하지 않은 한 명의 솔로 참가자입니다.
    /// 게임 방이 종료되면 별도의 파티 상태를 남기지 않습니다.
    /// </summary>
    SoloPlayer = 1,
}
