namespace CoopGameServer.Domain.Inventories;

/// <summary>
/// 한 플레이어가 보유한 한 종류의 아이템과 그 수량을 나타냅니다.
/// </summary>
/// <remarks>
/// 아직 아이템 마스터(Item Master, 아이템 이름·등급·효과를 정의하는 기준표)는 만들지 않았습니다.
/// 따라서 ItemId는 예시용 정수 식별자만 사용하며, 이후 아이템 마스터 테이블을 추가할 때 외래 키로 연결합니다.
/// </remarks>
public sealed class InventoryItem
{
    /// <summary>
    /// EF Core가 데이터베이스 행에서 객체를 복원할 때 사용하는 생성자입니다.
    /// </summary>
    private InventoryItem()
    {
    }

    /// <summary>
    /// 플레이어에게 최초로 지급되는 아이템 보유 정보를 만듭니다.
    /// </summary>
    /// <param name="playerId">아이템을 보유한 플레이어의 식별자입니다.</param>
    /// <param name="itemId">아이템 종류를 나타내는 양수 정수 식별자입니다.</param>
    /// <param name="quantity">최초 보유 수량이며 반드시 1 이상이어야 합니다.</param>
    /// <param name="createdAt">UTC 기준 생성 시각입니다.</param>
    public InventoryItem(Guid playerId, int itemId, int quantity, DateTimeOffset createdAt)
    {
        ValidatePlayerId(playerId);
        ValidateItemId(itemId);
        ValidatePositiveQuantity(quantity);

        PlayerId = playerId;
        ItemId = itemId;
        Quantity = quantity;
        UpdatedAt = createdAt;
    }

    /// <summary>
    /// 아이템 소유자를 식별합니다. ItemId와 함께 복합 기본 키(Composite Primary Key)를 구성합니다.
    /// </summary>
    public Guid PlayerId { get; private set; }

    /// <summary>
    /// 아이템 종류를 나타내는 정수 식별자입니다.
    /// </summary>
    public int ItemId { get; private set; }

    /// <summary>
    /// 현재 보유 수량입니다. 수량이 0인 행은 저장하지 않고 삭제로 표현할 예정입니다.
    /// </summary>
    public int Quantity { get; private set; }

    /// <summary>
    /// 수량이 마지막으로 변경된 UTC 기준 시각입니다.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// 이미 보유한 아이템에 양수 수량을 추가합니다.
    /// </summary>
    /// <param name="amount">추가할 수량이며 반드시 1 이상이어야 합니다.</param>
    /// <param name="updatedAt">UTC 기준 변경 시각입니다.</param>
    public void AddQuantity(int amount, DateTimeOffset updatedAt)
    {
        ValidatePositiveQuantity(amount);

        // int 범위를 넘어가는 비정상 수량은 조용히 변형하지 않고 예외로 처리합니다.
        Quantity = checked(Quantity + amount);
        UpdatedAt = updatedAt;
    }

    private static void ValidatePlayerId(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("플레이어 식별자는 비어 있을 수 없습니다.", nameof(playerId));
        }
    }

    private static void ValidateItemId(int itemId)
    {
        if (itemId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemId), "아이템 식별자는 1 이상이어야 합니다.");
        }
    }

    private static void ValidatePositiveQuantity(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "아이템 수량은 1 이상이어야 합니다.");
        }
    }
}
