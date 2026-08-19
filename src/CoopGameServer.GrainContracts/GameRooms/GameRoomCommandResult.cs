using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>게임 방 변경 명령의 처리 결과입니다.</summary>
/// <param name="IsReplay">같은 requestId의 최초 결과를 재생한 경우 true입니다.</param>
/// <param name="Error">게임 방 명령의 성공 또는 실패 이유입니다.</param>
/// <param name="Room">명령 처리 직후의 방 스냅샷이며, 방이 생성되기 전에는 null일 수 있습니다.</param>
/// <param name="FailedPartyId">파티 상태 전이에 실패했다면 해당 파티 식별자입니다.</param>
/// <param name="PartyError">PartyGrain이 반환한 세부 실패 이유입니다.</param>
[GenerateSerializer]
public sealed record GameRoomCommandResult(
    [property: Id(0)] bool IsReplay,
    [property: Id(1)] GameRoomCommandError Error,
    [property: Id(2)] GameRoomSnapshot? Room,
    [property: Id(3)] Guid? FailedPartyId,
    [property: Id(4)] PartyCommandError? PartyError);
