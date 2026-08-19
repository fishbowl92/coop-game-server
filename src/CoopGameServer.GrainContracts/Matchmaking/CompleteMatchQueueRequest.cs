namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>
/// 완료된 게임 방에 배정됐던 모든 티켓을 현재 매칭에서 해제하기 위한 내부 요청입니다.
/// </summary>
/// <param name="RequestId">같은 완료 처리를 재시도할 때 다시 사용하는 멱등성 식별자입니다.</param>
/// <param name="RoomId">완료되어 티켓을 해제할 게임 방 식별자입니다.</param>
[GenerateSerializer]
public sealed record CompleteMatchQueueRequest(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] Guid RoomId);
