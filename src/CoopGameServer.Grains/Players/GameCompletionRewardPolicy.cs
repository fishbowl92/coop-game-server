using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Grains.Players;

/// <summary>
/// GameRoom이 확정한 게임 결과를 서버 소유의 보상 수치로 변환하는 버전 고정 정책입니다.
/// </summary>
/// <remarks>
/// 클라이언트나 GameRoom 명령에는 골드·아이템 수량을 넣지 않습니다.
/// 같은 QueueKey·결과·정책 버전은 배포가 바뀐 뒤에도 같은 보상을 뜻해야
/// 재시도 시 PostgreSQL의 기존 멱등성 결과와 충돌하지 않습니다.
/// </remarks>
internal static class GameCompletionRewardPolicy
{
    /// <summary>현재 구현된 4인 협동 던전 일반 난이도 Queue 식별자입니다.</summary>
    private const string CoopDungeonNormalV1QueueKey = "coop-dungeon-normal-v1";

    /// <summary>일반 협동 던전의 첫 번째 불변 보상 정책 버전입니다.</summary>
    private const int CoopDungeonNormalPolicyVersion = 1;

    /// <summary>승리 시 지급하는 골드입니다.</summary>
    private const long VictoryGoldAmount = 500;

    /// <summary>승리 시 지급하는 포트폴리오용 시험 아이템 식별자입니다.</summary>
    private const int VictoryItemId = 1001;

    /// <summary>승리 시 지급하는 시험 아이템 수량입니다.</summary>
    private const int VictoryItemQuantity = 1;

    /// <summary>
    /// 지원하는 정책이면 true와 지급 또는 무지급 결정을 반환하고, 지원하지 않으면 false를 반환합니다.
    /// </summary>
    /// <param name="command">GameRoom이 전달한 서버 확정 게임 완료 명령입니다.</param>
    /// <param name="reward">승리 보상이며 패배·취소처럼 정상 무보상이면 null입니다.</param>
    internal static bool TryEvaluate(
        CompletePlayerGameCommand command,
        out GameCompletionReward? reward)
    {
        ArgumentNullException.ThrowIfNull(command);

        reward = null;

        if (!string.Equals(
                command.QueueKey,
                CoopDungeonNormalV1QueueKey,
                StringComparison.Ordinal) ||
            command.RewardPolicyVersion != CoopDungeonNormalPolicyVersion)
        {
            return false;
        }

        switch (command.Outcome)
        {
            case GameOutcome.Victory:
                reward = new GameCompletionReward(
                    VictoryGoldAmount,
                    VictoryItemId,
                    VictoryItemQuantity,
                    CreateReason(command));
                return true;

            case GameOutcome.Defeat:
            case GameOutcome.Cancelled:
                // 무보상도 정책 평가가 성공한 정상 결과입니다. null은 실패가 아니라
                // IRewardWriter를 호출할 지급 내용이 없다는 뜻으로만 사용합니다.
                return true;

            default:
                return false;
        }
    }

    /// <summary>감사 이력에서 방·Queue·정책·결과를 확인할 수 있는 고정 사유를 만듭니다.</summary>
    private static string CreateReason(CompletePlayerGameCommand command)
    {
        return $"game-room:{command.RoomId:D}:{command.QueueKey}:v{command.RewardPolicyVersion}:victory";
    }
}

/// <summary>정책이 계산한 실제 지급 수치입니다.</summary>
/// <param name="GoldAmount">추가할 골드 수량입니다.</param>
/// <param name="ItemId">추가할 아이템 식별자입니다.</param>
/// <param name="ItemQuantity">추가할 아이템 수량입니다.</param>
/// <param name="Reason">PostgreSQL 보상 감사 이력에 저장할 서버 측 사유입니다.</param>
internal sealed record GameCompletionReward(
    long GoldAmount,
    int? ItemId,
    int? ItemQuantity,
    string Reason);
