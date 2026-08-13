namespace CoopGameServer.Domain.Rewards;

/// <summary>
/// 보상 지급 요청이 실제로 적용되었다는 변경 불가능한 기록입니다.
/// </summary>
/// <remarks>
/// RequestId는 멱등성 키(Idempotency Key, 같은 요청을 여러 번 받아도 결과를 한 번만 적용하기 위한 고유 키)입니다.
/// DB의 UNIQUE(고유) 제약 조건이 같은 RequestId의 기록을 두 번 저장하지 못하게 하므로,
/// 네트워크 재시도나 동시 요청으로 보상이 중복 지급되는 일을 막는 근거가 됩니다.
/// </remarks>
public sealed class RewardAudit
{
    /// <summary>
    /// 보상 사유 텍스트의 최대 길이입니다. DB 열 길이와 도메인 검증에 같은 값을 사용합니다.
    /// </summary>
    public const int MaxReasonLength = 100;

    /// <summary>
    /// EF Core가 데이터베이스 행에서 객체를 복원할 때 사용하는 생성자입니다.
    /// </summary>
    private RewardAudit()
    {
    }

    /// <summary>
    /// 골드와 선택적 아이템 지급을 기록할 보상 감사(Audit, 변경 이력을 남기는 기록)를 생성합니다.
    /// </summary>
    /// <param name="id">이 감사 기록 자체의 고유 식별자입니다.</param>
    /// <param name="requestId">재시도 중복을 막는 멱등성 키입니다.</param>
    /// <param name="playerId">보상을 받는 플레이어 식별자입니다.</param>
    /// <param name="goldAmount">지급 골드입니다. 아이템 보상이 있으면 0일 수 있습니다.</param>
    /// <param name="itemId">지급 아이템 종류입니다. 아이템이 없으면 null입니다.</param>
    /// <param name="itemQuantity">지급 아이템 수량입니다. 아이템이 없으면 null입니다.</param>
    /// <param name="reason">보상 지급 사유입니다.</param>
    /// <param name="createdAt">UTC 기준 기록 시각입니다.</param>
    public RewardAudit(
        Guid id,
        Guid requestId,
        Guid playerId,
        long goldAmount,
        int? itemId,
        int? itemQuantity,
        string reason,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("보상 기록 식별자는 비어 있을 수 없습니다.", nameof(id));
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("멱등성 키는 비어 있을 수 없습니다.", nameof(requestId));
        }

        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("플레이어 식별자는 비어 있을 수 없습니다.", nameof(playerId));
        }

        if (goldAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(goldAmount), "지급 골드는 음수일 수 없습니다.");
        }

        ValidateItemReward(itemId, itemQuantity);

        if (goldAmount == 0 && itemId is null)
        {
            throw new ArgumentException("골드 또는 아이템 중 하나 이상을 지급해야 합니다.", nameof(goldAmount));
        }

        Id = id;
        RequestId = requestId;
        PlayerId = playerId;
        GoldAmount = goldAmount;
        ItemId = itemId;
        ItemQuantity = itemQuantity;
        Reason = NormalizeReason(reason);
        CreatedAt = createdAt;
    }

    /// <summary>
    /// 보상 감사 기록의 기본 키입니다.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// 동일 요청을 한 번만 처리하게 만드는 멱등성 키입니다.
    /// </summary>
    public Guid RequestId { get; private set; }

    /// <summary>
    /// 보상을 받는 플레이어 식별자입니다.
    /// </summary>
    public Guid PlayerId { get; private set; }

    /// <summary>
    /// 지급된 골드 양입니다. 0 이상입니다.
    /// </summary>
    public long GoldAmount { get; private set; }

    /// <summary>
    /// 지급된 아이템 종류입니다. 아이템 보상이 없으면 null입니다.
    /// </summary>
    public int? ItemId { get; private set; }

    /// <summary>
    /// 지급된 아이템 수량입니다. 아이템 보상이 없으면 null입니다.
    /// </summary>
    public int? ItemQuantity { get; private set; }

    /// <summary>
    /// 보상이 지급된 업무상 이유입니다.
    /// </summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// 이 보상 기록이 생성된 UTC 기준 시각입니다.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    private static void ValidateItemReward(int? itemId, int? itemQuantity)
    {
        // 아이템 종류와 수량은 항상 함께 있어야 합니다. 하나만 있으면 의미가 모호합니다.
        if (itemId is null && itemQuantity is null)
        {
            return;
        }

        if (itemId is null || itemQuantity is null)
        {
            throw new ArgumentException("아이템 종류와 수량은 함께 제공해야 합니다.");
        }

        if (itemId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemId), "아이템 식별자는 1 이상이어야 합니다.");
        }

        if (itemQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemQuantity), "아이템 수량은 1 이상이어야 합니다.");
        }
    }

    private static string NormalizeReason(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var normalizedReason = reason.Trim();

        if (normalizedReason.Length == 0)
        {
            throw new ArgumentException("보상 사유는 공백만으로 구성할 수 없습니다.", nameof(reason));
        }

        if (normalizedReason.Length > MaxReasonLength)
        {
            throw new ArgumentException(
                $"보상 사유는 {MaxReasonLength}자 이하여야 합니다.",
                nameof(reason));
        }

        return normalizedReason;
    }
}
