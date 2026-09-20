using CoopGameServer.Contracts.Rewards;

namespace CoopGameServer.Admin.Services;

/// <summary>편집 중인 입력과, 결과를 확인할 때까지 변경할 수 없는 전송 요청을 구분합니다.</summary>
public sealed class AdminRewardSubmissionState
{
    public Guid RequestId { get; private set; } = Guid.NewGuid();
    public long GoldAmount { get; set; }
    public int? ItemId { get; set; }
    public int? ItemQuantity { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>응답 유실 뒤에도 같은 관리자·대상·내용으로만 재시도할 최초 요청입니다.</summary>
    public PendingAdminReward? Pending { get; private set; }

    /// <summary>최초 전송 내용을 고정합니다. 이미 전송했다면 폼 편집값 대신 보관한 요청을 반환합니다.</summary>
    /// <param name="playerId">최초 전송 대상입니다. 미확정 요청의 대상을 바꿀 수 없습니다.</param>
    /// <param name="administratorLoginId">API 로그인 응답의 정규화된 관리자 로그인 ID입니다.</param>
    public PendingAdminReward BeginSubmission(Guid playerId, string administratorLoginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorLoginId);
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("보상 대상이 필요합니다.", nameof(playerId));
        }

        if (Pending is not null)
        {
            if (Pending.PlayerId != playerId ||
                !string.Equals(Pending.AdministratorLoginId, administratorLoginId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("미확정 요청의 관리자와 대상을 바꿀 수 없습니다.");
            }

            return Pending;
        }

        Pending = new PendingAdminReward(
            playerId, administratorLoginId,
            new GrantRewardRequest(RequestId, GoldAmount, ItemId, ItemQuantity, Reason.Trim()));
        return Pending;
    }

    /// <summary>이전 미확정 시도가 없는 최초 요청을 서버가 명확히 거절했을 때만 편집을 다시 허용합니다.</summary>
    public void ReleaseRejectedSubmission() => Pending = null;

    /// <summary>서버가 적용 또는 재생 성공을 확인한 뒤에만 다음 작업용 식별자를 만듭니다.</summary>
    public void ConfirmSuccess()
    {
        Pending = null;
        RequestId = Guid.NewGuid();
        GoldAmount = 0;
        ItemId = null;
        ItemQuantity = null;
        Reason = string.Empty;
    }
}

/// <summary>재시도에서 함께 유지해야 하는 대상·실행 계정·보상 본문입니다.</summary>
public sealed record PendingAdminReward(
    Guid PlayerId,
    string AdministratorLoginId,
    GrantRewardRequest Request);
