namespace CoopGameServer.GrainContracts.Parties;

/// <summary>
/// 파티 한 개의 생성, 가입, 탈퇴, 해산 규칙을 처리하는 Orleans Grain 계약입니다.
/// </summary>
/// <remarks>
/// Grain의 Guid 기본 키가 partyId입니다. API가 partyId를 생성한 뒤
/// <c>GetGrain&lt;IPartyGrain&gt;(partyId)</c>로 같은 논리적 파티를 호출하게 됩니다.
/// </remarks>
public interface IPartyGrain : IGrainWithGuidKey
{
    /// <summary>
    /// 아직 사용되지 않은 partyId로 파티를 만들고 최초 멤버를 리더로 지정합니다.
    /// </summary>
    /// <param name="requestId">같은 변경 요청의 재전송을 식별하는 고유 번호입니다.</param>
    /// <param name="leaderPlayerId">파티를 만들며 리더가 될 플레이어 식별자입니다.</param>
    /// <returns>명령 적용 여부와 처리 후 파티 상태입니다.</returns>
    Task<PartyCommandResult> CreateAsync(Guid requestId, Guid leaderPlayerId);

    /// <summary>
    /// 현재 파티 상태를 조회합니다.
    /// </summary>
    /// <returns>생성된 적이 없으면 null, 그렇지 않으면 활성 또는 해산 상태입니다.</returns>
    Task<PartySnapshot?> GetAsync();

    /// <summary>
    /// 플레이어 한 명을 파티에 가입시킵니다.
    /// </summary>
    /// <param name="requestId">같은 변경 요청의 재전송을 식별하는 고유 번호입니다.</param>
    /// <param name="playerId">가입할 플레이어 식별자입니다.</param>
    /// <returns>명령 적용 여부와 처리 후 파티 상태입니다.</returns>
    Task<PartyCommandResult> JoinAsync(Guid requestId, Guid playerId);

    /// <summary>
    /// 플레이어 한 명을 파티에서 탈퇴시킵니다.
    /// </summary>
    /// <param name="requestId">같은 변경 요청의 재전송을 식별하는 고유 번호입니다.</param>
    /// <param name="playerId">탈퇴할 플레이어 식별자입니다.</param>
    /// <returns>명령 적용 여부와 처리 후 파티 상태입니다.</returns>
    Task<PartyCommandResult> LeaveAsync(Guid requestId, Guid playerId);

    /// <summary>
    /// 현재 리더가 파티를 명시적으로 해산합니다.
    /// </summary>
    /// <param name="requestId">같은 변경 요청의 재전송을 식별하는 고유 번호입니다.</param>
    /// <param name="leaderPlayerId">리더 권한을 확인할 플레이어 식별자입니다.</param>
    /// <returns>명령 적용 여부와 해산된 파티 상태입니다.</returns>
    Task<PartyCommandResult> DisbandAsync(Guid requestId, Guid leaderPlayerId);
}
