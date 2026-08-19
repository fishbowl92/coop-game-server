namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>매칭 완료 뒤 한 게임 방이 거치는 생명 주기 상태입니다.</summary>
public enum GameRoomLifecycle
{
    /// <summary>4인 참가자 구성이 확정됐지만 게임 플레이는 아직 시작하지 않은 상태입니다.</summary>
    Ready = 0,

    /// <summary>사전 구성 파티를 포함한 참가자들이 게임을 진행 중인 상태입니다.</summary>
    InGame = 1,

    /// <summary>게임이 끝났으며 같은 roomId로 다시 시작할 수 없는 최종 상태입니다.</summary>
    Completed = 2,
}
