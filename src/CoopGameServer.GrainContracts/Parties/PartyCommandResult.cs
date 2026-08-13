namespace CoopGameServer.GrainContracts.Parties;

/// <summary>
/// 파티 상태 변경 명령의 결과입니다.
/// </summary>
/// <param name="IsReplay">true면 같은 requestId와 같은 내용의 최초 결과를 다시 반환한 것입니다.</param>
/// <param name="Error"><see cref="PartyCommandError.None"/>이면 명령이 성공했습니다.</param>
/// <param name="Party">명령을 처음 처리한 직후의 파티 상태입니다.</param>
[GenerateSerializer]
public sealed record PartyCommandResult(
    [property: Id(0)] bool IsReplay,
    [property: Id(1)] PartyCommandError Error,
    [property: Id(2)] PartySnapshot? Party);
