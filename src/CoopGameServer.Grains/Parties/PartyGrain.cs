using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.Grains.Parties;

/// <summary>
/// 파티 한 개의 명령을 Orleans 실행 순서 안에서 처리하는 Grain 구현체입니다.
/// </summary>
/// <remarks>
/// Orleans는 기본 Grain 호출을 한 번에 하나씩 처리하므로 같은 파티의 동시 가입 요청도
/// <see cref="PartyState"/>의 정원 검사를 순서대로 통과합니다.
/// 현재 상태는 활성화 메모리에만 있으며 영속화는 후속 단계입니다.
/// </remarks>
public sealed class PartyGrain : Grain, IPartyGrain
{
    private readonly PartyState _state = new();

    /// <inheritdoc />
    public Task<PartyCommandResult> CreateAsync(Guid requestId, Guid leaderPlayerId)
    {
        return Task.FromResult(_state.Create(GetPartyId(), requestId, leaderPlayerId));
    }

    /// <inheritdoc />
    public Task<PartySnapshot?> GetAsync()
    {
        return Task.FromResult(_state.Get(GetPartyId()));
    }

    /// <inheritdoc />
    public Task<PartyCommandResult> JoinAsync(Guid requestId, Guid playerId)
    {
        return Task.FromResult(_state.Join(GetPartyId(), requestId, playerId));
    }

    /// <inheritdoc />
    public Task<PartyCommandResult> LeaveAsync(Guid requestId, Guid playerId)
    {
        return Task.FromResult(_state.Leave(GetPartyId(), requestId, playerId));
    }

    /// <inheritdoc />
    public Task<PartyCommandResult> DisbandAsync(Guid requestId, Guid leaderPlayerId)
    {
        return Task.FromResult(_state.Disband(GetPartyId(), requestId, leaderPlayerId));
    }

    /// <summary>
    /// Orleans가 Grain 참조에 부여한 Guid 기본 키를 partyId로 읽습니다.
    /// </summary>
    private Guid GetPartyId() => this.GetPrimaryKey();
}
