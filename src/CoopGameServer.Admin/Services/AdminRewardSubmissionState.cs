using CoopGameServer.Contracts.Rewards;

namespace CoopGameServer.Admin.Services;

/// <summary>응답을 잃은 재시도에서도 같은 멱등성 키를 유지하는 보상 폼 상태입니다.</summary>
public sealed class AdminRewardSubmissionState
{
    public Guid RequestId { get; private set; } = Guid.NewGuid();
    public long GoldAmount { get; set; }
    public int? ItemId { get; set; }
    public int? ItemQuantity { get; set; }
    public string Reason { get; set; } = string.Empty;

    public GrantRewardRequest CreateRequest() =>
        new(RequestId, GoldAmount, ItemId, ItemQuantity, Reason);

    /// <summary>서버가 적용 또는 재생 성공을 확인한 뒤에만 다음 작업용 식별자를 만듭니다.</summary>
    public void ConfirmSuccess()
    {
        RequestId = Guid.NewGuid();
        GoldAmount = 0;
        ItemId = null;
        ItemQuantity = null;
        Reason = string.Empty;
    }
}
