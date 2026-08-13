using CoopGameServer.Domain.Rewards;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>
/// 보상 지급 서비스가 컨트롤러에 전달하는 내부 처리 결과입니다.
/// </summary>
/// <param name="RewardAudit">이번에 적용했거나 기존에 찾아낸 보상 이력입니다.</param>
/// <param name="IsReplay">같은 멱등성 키의 기존 처리 결과인지 나타냅니다.</param>
public sealed record GrantRewardResult(RewardAudit RewardAudit, bool IsReplay);
