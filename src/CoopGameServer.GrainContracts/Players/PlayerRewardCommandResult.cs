namespace CoopGameServer.GrainContracts.Players;

/// <summary>PlayerGrain 보상 명령의 처리 결과입니다.</summary>
/// <param name="IsReplay">같은 requestId로 확정된 기존 결과를 재생했다면 true입니다.</param>
/// <param name="Status">적용·정상 무보상·거부 중 하나인 업무 상태입니다.</param>
/// <param name="Error">거부 이유이며 정상 결과에서는 None입니다.</param>
/// <param name="Receipt">실제 적용 또는 재생된 보상 영수증이며 Applied 상태에서만 존재합니다.</param>
/// <remarks>
/// DB 연결 끊김이나 시간 초과 같은 기반시설 장애는 Rejected로 감추지 않고 예외로 전달합니다.
/// 호출자는 이를 정상적인 업무 거부와 구분해 재시도 여부를 결정할 수 있습니다.
/// </remarks>
[GenerateSerializer]
public sealed record PlayerRewardCommandResult(
    [property: Id(0)] bool IsReplay,
    [property: Id(1)] PlayerRewardCommandStatus Status,
    [property: Id(2)] PlayerRewardCommandError Error,
    [property: Id(3)] PlayerRewardReceipt? Receipt);
