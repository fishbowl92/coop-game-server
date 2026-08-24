using CoopGameServer.Contracts.Rewards;
using CoopGameServer.Persistence.Rewards;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>
/// HTTP 보상 요청을 Persistence 보상 명령으로 변환하는 임시 API 어댑터입니다.
/// </summary>
/// <remarks>
/// 실제 멱등성·행 잠금·Transaction은 <see cref="PostgreSqlRewardWriter"/>가 전담합니다.
/// 이 어댑터는 RewardsController를 PlayerGrain 호출로 전환하는 다음 단계까지 기존 HTTP 계약을 보존합니다.
/// </remarks>
public sealed class RewardService
{
    private readonly IRewardWriter _rewardWriter;

    /// <summary>
    /// API 요청 형식과 Persistence 명령을 연결할 Writer를 주입받습니다.
    /// </summary>
    /// <param name="rewardWriter">보상 DB 변경을 수행하는 영속성 경계입니다.</param>
    public RewardService(IRewardWriter rewardWriter)
    {
        ArgumentNullException.ThrowIfNull(rewardWriter);
        _rewardWriter = rewardWriter;
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
    /// <returns>플레이어가 없으면 null, 그 외에는 신규 적용 또는 재전송 결과를 반환합니다.</returns>
    /// <exception cref="ArgumentException">보상 금액·아이템·사유·멱등성 키가 유효하지 않으면 발생합니다.</exception>
    /// <exception cref="IdempotencyKeyConflictException">같은 키가 다른 보상 내용에 이미 사용되면 발생합니다.</exception>
    public async Task<GrantRewardResult?> GrantAsync(
        Guid playerId,
        GrantRewardRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var command = new RewardWriteCommand(
            request.RequestId ?? Guid.Empty,
            playerId,
            request.GoldAmount,
            request.ItemId,
            request.ItemQuantity,
            request.Reason!);

        // 같은 프로세스에서 직접 실행한 Writer Task를 끝까지 await하여 성공과 실패를 반드시 관찰합니다.
        // 다음 단계에서 Orleans Grain이 작업을 소유하게 되면 HTTP 응답 대기만 별도로 취소할 수 있습니다.
        var writeResult = await _rewardWriter.WriteAsync(command);

        return writeResult.Error switch
        {
            RewardWriteError.None => ToGrantRewardResult(writeResult),
            RewardWriteError.PlayerNotFound => null,
            RewardWriteError.IdempotencyConflict => throw new IdempotencyKeyConflictException(command.RequestId),
            _ => throw new InvalidOperationException($"지원하지 않는 보상 쓰기 오류입니다: {writeResult.Error}"),
        };
    }

    /// <summary>정상 Writer 결과를 기존 Controller용 내부 결과로 변환합니다.</summary>
    private static GrantRewardResult ToGrantRewardResult(RewardWriteResult writeResult)
    {
        var receipt = writeResult.Receipt
            ?? throw new InvalidOperationException("성공한 보상 쓰기 결과에 영수증이 없습니다.");

        return new GrantRewardResult(receipt, writeResult.IsReplay);
    }
}
