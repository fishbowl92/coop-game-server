namespace CoopGameServer.Grains.GameRooms;

/// <summary>
/// 게임 결과 자동 복구를 한 번 실행한 뒤 발견·성공·실패한 방 수를 반환합니다.
/// </summary>
/// <param name="DiscoveredRoomCount">이번 조회에서 발견한 서로 다른 게임 방 수입니다.</param>
/// <param name="SucceededRoomCount">GameRoomGrain 호출이 정상 종료된 방 수입니다.</param>
/// <param name="FailedRoomCount">호출 중 예외가 발생해 다음 주기에 다시 확인할 방 수입니다.</param>
public sealed record GameRoomRecoveryBatchResult(
    int DiscoveredRoomCount,
    int SucceededRoomCount,
    int FailedRoomCount);
