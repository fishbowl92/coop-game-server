using CoopGameServer.Contracts.Rewards;
using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>
/// HTTP 보상 요청을 PlayerGrain 관리자 보상 명령으로 변환하는 API 어댑터입니다.
/// </summary>
/// <remarks>
/// 멱등성·행 잠금·Transaction은 Silo의 PlayerGrain과 PostgreSQL Writer가 전담합니다.
/// 이 서비스는 HTTP DTO가 Orleans 계약에 직접 섞이지 않도록 두 형식 사이의 변환만 담당합니다.
/// </remarks>
public sealed class RewardService
{
    private readonly IPlayerGrainClient _playerGrainClient;

    /// <summary>
    /// API 요청 형식과 PlayerGrain 명령을 연결할 Grain Client를 주입받습니다.
    /// </summary>
    /// <param name="playerGrainClient">Player ID에 해당하는 Grain으로 보상 명령을 전달하는 호출 경계입니다.</param>
    public RewardService(IPlayerGrainClient playerGrainClient)
    {
        ArgumentNullException.ThrowIfNull(playerGrainClient);
        _playerGrainClient = playerGrainClient;
    }

    /// <summary>
    /// 한 번의 보상 요청을 처리하거나, 같은 멱등성 키의 기존 결과를 반환합니다.
    /// </summary>
    /// <param name="playerId">보상을 받는 플레이어 식별자입니다.</param>
    /// <param name="request">골드·아이템·멱등성 키·사유를 담은 요청입니다.</param>
    /// <param name="cancellationToken">
    /// 작업을 시작하기 전에 요청이 이미 취소됐는지 확인하는 토큰입니다.
    /// 보상 쓰기가 시작된 뒤에는 멱등성 결과를 확정하기 위해 중간 취소하지 않습니다.
    /// </param>
    /// <returns>PlayerGrain이 확정한 적용·재생·업무 거부 결과입니다.</returns>
    public async Task<PlayerRewardCommandResult> GrantAsync(
        Guid playerId,
        GrantRewardRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var command = new GrantPlayerRewardCommand(
            request.RequestId ?? Guid.Empty,
            request.GoldAmount,
            request.ItemId,
            request.ItemQuantity,
            request.Reason ?? string.Empty);

        // 이미 취소된 요청은 Grain 명령을 시작하지 않습니다. 호출이 시작된 뒤에는 WaitAsync만 취소되며,
        // 기반 Grain Task(실제 Grain 작업)는 HTTP 연결과 무관하게 Silo에서 끝까지 실행됩니다.
        var grainTask = _playerGrainClient.GrantAdminRewardAsync(playerId, command);
        return await grainTask.WaitAsync(cancellationToken);
    }
}
