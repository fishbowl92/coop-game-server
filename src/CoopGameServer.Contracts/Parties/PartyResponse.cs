namespace CoopGameServer.Contracts.Parties;

/// <summary>
/// 파티 생성·조회·변경 API가 외부 클라이언트에 반환하는 파티 상태입니다.
/// </summary>
/// <param name="PartyId">파티를 식별하는 서버 생성 Guid입니다.</param>
/// <param name="Lifecycle">Active·MatchQueued·InGame·Disbanded 중 현재 파티 생명주기입니다.</param>
/// <param name="LeaderPlayerId">현재 리더이며, 해산된 파티라면 null입니다.</param>
/// <param name="MemberPlayerIds">가입 순서를 유지한 현재 멤버 식별자 배열입니다.</param>
/// <param name="CurrentRoomId">게임 중인 방 식별자이며, InGame 상태가 아니면 null입니다.</param>
/// <param name="IsReplay">같은 requestId의 최초 결과를 재생한 응답이면 true입니다.</param>
public sealed record PartyResponse(
    Guid PartyId,
    string Lifecycle,
    Guid? LeaderPlayerId,
    Guid[] MemberPlayerIds,
    Guid? CurrentRoomId,
    bool IsReplay);
