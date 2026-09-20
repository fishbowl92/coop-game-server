using System.Net;
using CoopGameServer.Contracts.Administration;
using CoopGameServer.Contracts.Players;
using CoopGameServer.Contracts.Rewards;

namespace CoopGameServer.Admin.Services;

/// <summary>운영 화면의 조회·지급 상태를 조정합니다. 지급 확정과 화면 갱신 성공은 별도로 보관합니다.</summary>
public sealed class AdminOperationsState(
    AdminApiClient api,
    AdminSession session,
    AdminRewardSubmissionState reward)
{
    /// <summary>한 대상에 대한 세 조회가 모두 성공했을 때만 교체하는 화면 데이터입니다.</summary>
    public AdminPlayerSelection? Selection { get; private set; }

    /// <summary>후속 조회나 다른 작업이 실패해도 유지하는 마지막 지급 성공 영수증입니다.</summary>
    public GrantRewardResponse? LastReceipt { get; private set; }
    public bool IsBusy { get; private set; }

    /// <summary>지급 후 최신 조회가 끝나기 전에는 이전 잔액을 근거로 새 보상을 지급하지 못하게 합니다.</summary>
    public bool RequiresRefresh { get; private set; }
    public string? Message { get; private set; }
    public string MessageStyle { get; private set; } = "info";

    public bool IsPendingForCurrentAdministrator => reward.Pending is { } pending &&
        session.IsAuthenticated &&
        string.Equals(pending.AdministratorLoginId, session.LoginId, StringComparison.Ordinal);
    public bool CanLookup => !IsBusy && session.IsAuthenticated && reward.Pending is null;
    public bool CanEditReward => !IsBusy && reward.Pending is null;
    public bool CanRefresh => !IsBusy && session.IsAuthenticated && (Selection is not null || LastReceipt is not null);
    public bool CanGrant => !IsBusy && session.IsAuthenticated &&
        (reward.Pending is not null
            ? IsPendingForCurrentAdministrator
            : Selection is not null && !RequiresRefresh);

    /// <summary>로그인 후에도 미확정 요청의 원래 계정은 유지합니다. 다른 계정으로 재전송하지 않습니다.</summary>
    public Task SignInAsync(string loginId, string password) => RunAsync(async () =>
    {
        // 만료 후 다른 관리자로 로그인해도 이전 계정의 선택·영수증을 새 세션에 섞지 않습니다.
        Selection = null;
        LastReceipt = null;
        RequiresRefresh = false;
        await api.SignInAsync(loginId, password);
        SetMessage(reward.Pending is not null && !IsPendingForCurrentAdministrator
            ? "미확정 보상 요청이 있습니다. 처음 전송한 관리자 계정으로 다시 로그인해 결과를 확인하세요."
            : "관리자 로그인이 완료되었습니다.", "info");
    });

    /// <summary>계정 화면 정보는 지우되 미확정 요청은 같은 계정의 재로그인을 위해 보존합니다.</summary>
    public void SignOut()
    {
        if (IsBusy) return;
        session.SignOut();
        Selection = null;
        LastReceipt = null;
        RequiresRefresh = false;
        SetMessage("로그아웃했습니다.", "info");
    }

    /// <summary>새 조회 시작 시 이전 선택을 해제하고, 기본 정보·진행도·이력을 모두 얻은 뒤 반영합니다.</summary>
    public Task LookupAsync(string query)
    {
        if (!CanLookup) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            Selection = null;
            RequiresRefresh = false;
            var player = await api.LookupPlayerAsync(query);
            Selection = await ReadSelectionAsync(player);
            SetMessage($"{player.Nickname} 플레이어를 조회했습니다.", "success");
        });
    }

    /// <summary>지급 요청 없이 화면 데이터만 다시 읽습니다. 실패하면 새 지급을 계속 차단합니다.</summary>
    public Task RefreshAsync()
    {
        if (!CanRefresh) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            RequiresRefresh = true;
            var player = Selection?.Player ?? await api.LookupPlayerAsync(LastReceipt!.PlayerId.ToString());
            Selection = await ReadSelectionAsync(player);
            RequiresRefresh = false;
            SetMessage("플레이어 정보와 보상 이력을 새로 조회했습니다.", "success");
        });
    }

    /// <summary>최초 요청을 고정해 지급하고, 확정 영수증을 먼저 보관한 뒤 후속 조회를 별도로 처리합니다.</summary>
    public Task GrantAsync()
    {
        if (!CanGrant) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var isRetry = reward.Pending is not null;
            var pending = reward.BeginSubmission(
                reward.Pending?.PlayerId ?? Selection!.Player.PlayerId,
                session.LoginId!);
            GrantRewardResponse receipt;
            try
            {
                receipt = await api.GrantRewardAsync(pending.PlayerId, pending.Request);
            }
            catch (AdminApiException exception) when (!isRetry && IsDefiniteRejection(exception.StatusCode))
            {
                // 최초 400/401/403/404는 이 API에서 영구 변경 전 거부입니다. 입력을 수정할 수 있습니다.
                // 이전 응답을 잃은 재시도의 거부는 최초 적용 여부를 증명하지 못하므로 해제하지 않습니다.
                reward.ReleaseRejectedSubmission();
                throw;
            }

            // 잘못된/불완전한 성공 응답으로 다른 요청을 확정하지 않습니다. 최초 요청을 유지합니다.
            if (receipt.RequestId != pending.Request.RequestId || receipt.PlayerId != pending.PlayerId ||
                receipt.GoldAmount != pending.Request.GoldAmount || receipt.ItemId != pending.Request.ItemId ||
                receipt.ItemQuantity != pending.Request.ItemQuantity ||
                !string.Equals(receipt.Reason, pending.Request.Reason, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("응답 영수증이 전송한 요청과 일치하지 않습니다. 같은 요청으로 결과를 다시 확인하세요.");
            }

            LastReceipt = receipt;
            reward.ConfirmSuccess();
            RequiresRefresh = true;
            SetMessage("보상 지급 결과를 확인했습니다.", "success");

            try
            {
                var player = Selection?.Player ?? await api.LookupPlayerAsync(pending.PlayerId.ToString());
                Selection = await ReadSelectionAsync(player);
                RequiresRefresh = false;
            }
            catch (Exception exception)
            {
                // 지급은 이미 확정됐습니다. 조회 실패를 지급 실패로 바꾸거나 보상을 다시 보내지 않습니다.
                SetMessage($"보상 지급은 완료됐지만 최신 정보 조회에 실패했습니다. ‘정보 새로고침’으로 조회만 다시 시도하세요. {exception.Message}", "warning");
            }
        });
    }

    private async Task<AdminPlayerSelection> ReadSelectionAsync(AdminPlayerLookupResponse player)
    {
        var progression = await api.GetProgressionAsync(player.PlayerId);
        var history = await api.GetRewardHistoryAsync(player.PlayerId);
        if (progression.PlayerId != player.PlayerId || history.Any(entry => entry.PlayerId != player.PlayerId))
        {
            throw new InvalidOperationException("조회 결과의 대상이 일치하지 않습니다.");
        }

        return new AdminPlayerSelection(player, progression, history);
    }

    /// <summary>조회·지급·로그인을 한 번에 하나만 수행해 대기 중 대상 변경과 중복 클릭을 차단합니다.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Message = null;
        try
        {
            await action();
        }
        catch (AdminApiException exception)
        {
            SetMessage($"API 오류 ({(int)exception.StatusCode}): {exception.Message}", "error");
        }
        catch (Exception exception)
        {
            SetMessage($"요청 결과를 확인하지 못했습니다: {exception.Message}", "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool IsDefiniteRejection(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    private void SetMessage(string message, string style)
    {
        Message = message;
        MessageStyle = style;
    }
}

/// <summary>한 대상의 조회 결과를 묶어 이전 대상의 잔액·이력이 섞이지 않도록 합니다.</summary>
public sealed record AdminPlayerSelection(
    AdminPlayerLookupResponse Player,
    PlayerProgressionResponse Progression,
    AdminRewardHistoryResponse[] History);
