namespace CoopGameServer.Api.Domain.Wallets;

/// <summary>
/// 플레이어 한 명의 골드(Gold, 게임 내 기본 재화) 잔액을 나타냅니다.
/// </summary>
/// <remarks>
/// 골드는 부동소수점(float, double)이 아니라 정수(long)로 저장합니다.
/// 금액에 소수점 오차가 생기지 않으며, PostgreSQL에서는 bigint 열에 대응합니다.
/// 실제 보상 지급 시에는 이 객체의 잔액 변경, 인벤토리 변경, 보상 기록을 하나의
/// 트랜잭션(Transaction, 여러 DB 변경을 모두 성공시키거나 모두 되돌리는 단위)으로 저장합니다.
/// </remarks>
public sealed class PlayerWallet
{
    /// <summary>
    /// EF Core(Entity Framework Core)가 데이터베이스 행에서 객체를 복원할 때 사용하는 생성자입니다.
    /// 일반 코드에서는 아래의 공개 생성자로만 지갑을 생성하여 기본 규칙을 지킵니다.
    /// </summary>
    private PlayerWallet()
    {
    }

    /// <summary>
    /// 비어 있는 골드 지갑을 생성합니다.
    /// </summary>
    /// <param name="playerId">지갑의 소유자인 플레이어 식별자입니다.</param>
    /// <param name="createdAt">UTC(Coordinated Universal Time, 협정 세계시) 기준 생성 시각입니다.</param>
    /// <exception cref="ArgumentException">플레이어 식별자가 비어 있으면 발생합니다.</exception>
    public PlayerWallet(Guid playerId, DateTimeOffset createdAt)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("플레이어 식별자는 비어 있을 수 없습니다.", nameof(playerId));
        }

        PlayerId = playerId;
        Gold = 0;
        UpdatedAt = createdAt;
    }

    /// <summary>
    /// 이 지갑을 소유한 플레이어의 식별자입니다.
    /// 한 플레이어는 지갑을 하나만 가지므로 DB에서는 기본 키(Primary Key, PK)로 사용합니다.
    /// </summary>
    public Guid PlayerId { get; private set; }

    /// <summary>
    /// 현재 보유 골드입니다. 음수 잔액은 허용하지 않습니다.
    /// </summary>
    public long Gold { get; private set; }

    /// <summary>
    /// 골드가 마지막으로 변경된 UTC 기준 시각입니다.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// 양수 골드를 지급하고 마지막 변경 시각을 갱신합니다.
    /// </summary>
    /// <param name="amount">지급할 골드 양이며 반드시 1 이상이어야 합니다.</param>
    /// <param name="updatedAt">UTC 기준 지급 시각입니다.</param>
    /// <exception cref="ArgumentOutOfRangeException">지급 양이 0 이하이면 발생합니다.</exception>
    /// <exception cref="OverflowException">지급 후 골드가 long 범위를 넘으면 발생합니다.</exception>
    public void AddGold(long amount, DateTimeOffset updatedAt)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "지급할 골드는 1 이상이어야 합니다.");
        }

        // checked는 long 최대값을 넘어 조용히 음수로 되돌아가는 오버플로(overflow)를 예외로 바꿉니다.
        Gold = checked(Gold + amount);
        UpdatedAt = updatedAt;
    }
}
