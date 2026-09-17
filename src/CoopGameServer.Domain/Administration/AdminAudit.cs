using CoopGameServer.Domain.Rewards;

namespace CoopGameServer.Domain.Administration;

/// <summary>
/// 권한 있는 관리자가 실행해 PostgreSQL에 최종 적용된 운영 작업의 변경 불가능한 기록입니다.
/// </summary>
/// <remarks>
/// 보상 지급에서는 같은 <see cref="RequestId"/>의 <see cref="RewardAudit"/>와 한 Transaction에 저장합니다.
/// 실패하거나 거부된 요청은 대상 데이터를 바꾸지 않으므로 이 성공 감사 테이블에 기록하지 않습니다.
/// </remarks>
public sealed class AdminAudit
{
    private AdminAudit()
    {
    }

    /// <summary>관리자 보상 지급이 최종 적용됐다는 감사 기록을 만듭니다.</summary>
    public AdminAudit(
        Guid id,
        Guid administratorAccountId,
        Guid targetPlayerId,
        Guid requestId,
        AdminAuditAction action,
        long goldAmount,
        int? itemId,
        int? itemQuantity,
        string reason,
        AdminAuditResult result,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("관리자 감사 식별자는 비어 있을 수 없습니다.", nameof(id));
        }

        if (administratorAccountId == Guid.Empty)
        {
            throw new ArgumentException("관리자 계정 식별자는 비어 있을 수 없습니다.", nameof(administratorAccountId));
        }

        if (targetPlayerId == Guid.Empty)
        {
            throw new ArgumentException("대상 플레이어 식별자는 비어 있을 수 없습니다.", nameof(targetPlayerId));
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("멱등성 키는 비어 있을 수 없습니다.", nameof(requestId));
        }

        if (action is not AdminAuditAction.GrantReward)
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "지원하지 않는 관리자 작업입니다.");
        }

        if (result is not AdminAuditResult.Applied)
        {
            throw new ArgumentOutOfRangeException(nameof(result), result, "저장 가능한 관리자 작업 결과가 아닙니다.");
        }

        // RewardAudit 생성자가 보상 모양과 사유를 동일하게 검증·정규화하게 하여
        // 두 감사 테이블에 서로 다른 의미의 Payload가 저장되지 않도록 합니다.
        var normalizedReward = new RewardAudit(
            Guid.NewGuid(),
            requestId,
            targetPlayerId,
            goldAmount,
            itemId,
            itemQuantity,
            reason,
            createdAt);

        Id = id;
        AdministratorAccountId = administratorAccountId;
        TargetPlayerId = targetPlayerId;
        RequestId = requestId;
        Action = action;
        GoldAmount = normalizedReward.GoldAmount;
        ItemId = normalizedReward.ItemId;
        ItemQuantity = normalizedReward.ItemQuantity;
        Reason = normalizedReward.Reason;
        Result = result;
        CreatedAt = createdAt;
    }

    /// <summary>감사 기록 자체의 식별자입니다.</summary>
    public Guid Id { get; private set; }

    /// <summary>검증된 JWT의 account_id Claim에서 가져온 관리자 계정 식별자입니다.</summary>
    public Guid AdministratorAccountId { get; private set; }

    /// <summary>운영 작업의 대상 Player 식별자입니다.</summary>
    public Guid TargetPlayerId { get; private set; }

    /// <summary>보상 감사 기록과 공유하는 멱등성 키입니다.</summary>
    public Guid RequestId { get; private set; }

    /// <summary>실행한 관리자 작업 종류입니다.</summary>
    public AdminAuditAction Action { get; private set; }

    /// <summary>정규화된 지급 골드입니다.</summary>
    public long GoldAmount { get; private set; }

    /// <summary>선택적 지급 아이템 식별자입니다.</summary>
    public int? ItemId { get; private set; }

    /// <summary>선택적 지급 아이템 수량입니다.</summary>
    public int? ItemQuantity { get; private set; }

    /// <summary>관리자가 입력해 RewardAudit과 동일하게 정규화된 지급 사유입니다.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>PostgreSQL에 최종 확정된 작업 결과입니다.</summary>
    public AdminAuditResult Result { get; private set; }

    /// <summary>보상과 관리자 감사가 함께 적용된 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
