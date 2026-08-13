namespace CoopGameServer.GrainContracts.Parties;

/// <summary>
/// 호출 시점에 복사한 파티의 읽기 전용 상태입니다.
/// </summary>
/// <param name="PartyId">API가 생성하고 Grain 기본 키로 사용하는 파티 식별자입니다.</param>
/// <param name="Lifecycle">현재 활성 또는 해산 상태입니다.</param>
/// <param name="LeaderPlayerId">현재 리더이며, 해산 상태에서는 null입니다.</param>
/// <param name="MemberPlayerIds">가입 순서를 보존한 멤버 배열이며, 리더 승계 순서의 기준입니다.</param>
[GenerateSerializer]
public sealed record PartySnapshot(
    [property: Id(0)] Guid PartyId,
    [property: Id(1)] PartyLifecycle Lifecycle,
    [property: Id(2)] Guid? LeaderPlayerId,
    [property: Id(3)] Guid[] MemberPlayerIds);
