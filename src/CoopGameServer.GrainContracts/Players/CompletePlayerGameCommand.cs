using CoopGameServer.GrainContracts.GameRooms;

namespace CoopGameServer.GrainContracts.Players;

/// <summary>GameRoomGrain이 확정한 게임 결과를 PlayerGrain에 전달하는 명령입니다.</summary>
/// <param name="RequestId">같은 완료 처리를 재전송해도 한 번만 반영하기 위한 멱등성 키입니다.</param>
/// <param name="RoomId">완료된 게임 방 식별자입니다.</param>
/// <param name="QueueKey">게임 모드와 난이도 같은 매칭 조건 식별자입니다.</param>
/// <param name="Outcome">클라이언트가 아닌 서버가 확정한 최종 게임 결과입니다.</param>
/// <param name="RewardPolicyVersion">결과를 보상으로 변환할 서버 정책 버전입니다.</param>
/// <remarks>
/// 골드·아이템 수량은 의도적으로 포함하지 않습니다. 클라이언트 입력이 아니라
/// 서버의 RewardPolicy(보상 정책)가 결과와 정책 버전을 보고 계산해야 하기 때문입니다.
/// </remarks>
[GenerateSerializer]
public sealed record CompletePlayerGameCommand(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] Guid RoomId,
    [property: Id(2)] string QueueKey,
    [property: Id(3)] GameOutcome Outcome,
    [property: Id(4)] int RewardPolicyVersion);
