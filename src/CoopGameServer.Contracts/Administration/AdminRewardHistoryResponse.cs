namespace CoopGameServer.Contracts.Administration;

/// <summary>관리자가 확인하는 보상 결과와 실행 주체를 합친 읽기 전용 응답입니다.</summary>
public sealed record AdminRewardHistoryResponse(
    Guid RewardAuditId,
    Guid RequestId,
    Guid PlayerId,
    long GoldAmount,
    int? ItemId,
    int? ItemQuantity,
    string Reason,
    DateTimeOffset CreatedAt,
    Guid? AdministratorAccountId,
    string? AdministratorLoginId);
