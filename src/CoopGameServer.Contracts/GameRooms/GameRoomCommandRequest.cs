namespace CoopGameServer.Contracts.GameRooms;

/// <summary>게임 방 시작 또는 완료 명령의 중복 실행을 막기 위한 외부 HTTP 요청입니다.</summary>
/// <param name="RequestId">같은 명령의 재전송을 식별하는 멱등성 요청 식별자입니다.</param>
public sealed record GameRoomCommandRequest(Guid RequestId);
