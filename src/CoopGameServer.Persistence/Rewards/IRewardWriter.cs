namespace CoopGameServer.Persistence.Rewards;

/// <summary>
/// 플레이어 보상을 PostgreSQL에 원자적으로 반영하는 영속성 경계입니다.
/// </summary>
/// <remarks>
/// PlayerGrain은 구체적인 PostgreSQL 구현 대신 이 인터페이스에 의존합니다.
/// 호출이 시작된 뒤에는 HTTP 연결 종료와 무관하게 멱등성 결과를 끝까지 확정해야 하므로
/// Orleans 명령 경계와 동일하게 외부 <see cref="CancellationToken"/>을 받지 않습니다.
/// </remarks>
public interface IRewardWriter
{
    /// <summary>보상을 새로 반영하거나 같은 멱등성 키로 확정된 기존 결과를 반환합니다.</summary>
    /// <param name="command">플레이어와 지급할 재화·아이템을 담은 영속성 명령입니다.</param>
    Task<RewardWriteResult> WriteAsync(RewardWriteCommand command);
}
