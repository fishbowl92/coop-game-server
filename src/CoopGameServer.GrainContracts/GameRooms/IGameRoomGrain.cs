using CoopGameServer.GrainContracts.Matchmaking;

namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>
/// 매칭으로 생성된 한 게임 방의 참가자와 시작·완료 상태를 순차 처리하는 Orleans Grain 계약입니다.
/// </summary>
/// <remarks>
/// Grain의 Guid 기본 키가 roomId입니다. 사전 구성 파티는 게임 종료 뒤 Active 상태로 돌아가 유지되고,
/// 솔로 참가자는 임시 파티를 만들지 않으므로 PartyGrain 상태 전이 대상에 포함되지 않습니다.
/// </remarks>
public interface IGameRoomGrain : IGrainWithGuidKey
{
    /// <summary>MatchQueueGrain의 4인 배정 결과로 아직 존재하지 않는 게임 방을 생성합니다.</summary>
    /// <param name="requestId">같은 생성 요청의 재전송을 식별하는 고유 번호입니다.</param>
    /// <param name="assignment">방·파티·플레이어 구성이 들어 있는 매칭 결과입니다.</param>
    Task<GameRoomCommandResult> CreateAsync(Guid requestId, MatchAssignment assignment);

    /// <summary>현재 게임 방 상태를 조회합니다. 생성된 적이 없으면 null입니다.</summary>
    Task<GameRoomSnapshot?> GetAsync();

    /// <summary>연결된 사전 구성 파티를 InGame으로 전환한 뒤 게임을 시작합니다.</summary>
    /// <param name="requestId">같은 시작 요청의 재전송을 식별하는 고유 번호입니다.</param>
    Task<GameRoomCommandResult> StartAsync(Guid requestId);

    /// <summary>최종 결과를 확정해 게임을 완료하고 사전 구성 파티를 멤버 그대로 Active 로비 상태로 되돌립니다.</summary>
    /// <param name="requestId">같은 완료 요청의 재전송을 식별하는 고유 번호입니다.</param>
    /// <param name="outcome">서버가 확정한 승리·패배·취소 중 하나의 경기 결과입니다.</param>
    Task<GameRoomCommandResult> CompleteAsync(Guid requestId, GameOutcome outcome);
}
