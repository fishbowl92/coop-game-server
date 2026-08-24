namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>
/// 서버가 확정한 한 게임 방의 최종 결과입니다.
/// </summary>
/// <remarks>
/// 숫자 값은 Orleans 직렬화 계약과 영속 데이터의 의미를 안정적으로 유지하기 위해 명시적으로 고정합니다.
/// 배포 뒤 기존 값의 숫자를 바꾸거나 다른 의미로 재사용하면 안 됩니다.
/// </remarks>
[GenerateSerializer]
public enum GameOutcome
{
    /// <summary>아직 결과가 확정되지 않았습니다.</summary>
    None = 0,

    /// <summary>플레이어 측이 전투에서 승리했습니다.</summary>
    Victory = 1,

    /// <summary>플레이어 측이 전투에서 패배했습니다.</summary>
    Defeat = 2,

    /// <summary>운영자 요청이나 최초 연결 시간 초과 등으로 게임이 취소됐습니다.</summary>
    Cancelled = 3,
}
