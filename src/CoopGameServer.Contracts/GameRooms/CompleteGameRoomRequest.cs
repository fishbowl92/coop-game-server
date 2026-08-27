namespace CoopGameServer.Contracts.GameRooms;

/// <summary>게임 방의 최종 결과를 확정하는 외부 HTTP 요청입니다.</summary>
/// <param name="RequestId">같은 완료 명령의 재전송을 식별하는 멱등성 요청 식별자입니다.</param>
/// <param name="Outcome">Victory·Defeat·Cancelled 중 하나의 서버 확정 결과 이름입니다.</param>
public sealed record CompleteGameRoomRequest(Guid RequestId, string Outcome);
