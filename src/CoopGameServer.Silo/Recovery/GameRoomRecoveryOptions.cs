namespace CoopGameServer.Silo.Recovery;

/// <summary>게임 결과 자동 복구 서비스의 조회 간격과 한 번의 처리량을 설정합니다.</summary>
public sealed class GameRoomRecoveryOptions
{
    /// <summary>설정 파일에서 값을 읽을 때 사용하는 구역 이름입니다.</summary>
    public const string SectionName = "GameRoomRecovery";

    /// <summary>복구 대상 DB 조회 사이의 기본 대기 시간입니다.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>한 번의 조회에서 깨울 서로 다른 게임 방의 최대 개수입니다.</summary>
    public int BatchSize { get; set; } = 100;
}
