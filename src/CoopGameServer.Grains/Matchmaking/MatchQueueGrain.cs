using CoopGameServer.GrainContracts.Matchmaking;

namespace CoopGameServer.Grains.Matchmaking;

/// <summary>
/// 한 매칭 조건의 대기열 명령을 순차 처리하는 Orleans Grain 구현체입니다.
/// </summary>
/// <remarks>
/// 같은 Grain에는 한 번에 하나의 요청만 실행되는 Orleans의 기본 실행 규칙이 적용됩니다.
/// 따라서 동시에 여러 파티가 등록되어도 대기 순서와 4인 조합 상태가 서로 덮어써지지 않습니다.
/// 현재 상태는 메모리 전용이며 PostgreSQL 영속화는 다음 작업 단위에서 추가합니다.
/// </remarks>
public sealed class MatchQueueGrain : Grain, IMatchQueueGrain
{
    private readonly MatchQueueState _state = new();

    /// <inheritdoc />
    public Task<MatchQueueCommandResult> EnqueueAsync(MatchQueueEntryRequest request)
    {
        return Task.FromResult(_state.Enqueue(this.GetPrimaryKeyString(), request));
    }

    /// <inheritdoc />
    public Task<MatchQueueCommandResult> CancelAsync(CancelMatchQueueRequest request)
    {
        return Task.FromResult(_state.Cancel(request));
    }

    /// <inheritdoc />
    public Task<MatchQueueTicket?> GetTicketAsync(Guid partyId)
    {
        return Task.FromResult(_state.GetTicket(partyId));
    }

    /// <inheritdoc />
    public Task<MatchQueueSnapshot> GetSnapshotAsync()
    {
        return Task.FromResult(_state.GetSnapshot(this.GetPrimaryKeyString()));
    }
}
