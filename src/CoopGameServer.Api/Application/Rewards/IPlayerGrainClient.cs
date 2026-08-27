using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>
/// API 애플리케이션 계층이 PlayerGrain에 보상 명령을 전달할 때 사용하는 작은 호출 경계입니다.
/// </summary>
/// <remarks>
/// Orleans의 큰 IGrainFactory 인터페이스를 Controller와 단위 테스트 전체에 노출하지 않습니다.
/// 또한 Grain 작업은 HTTP 연결 취소와 별개로 끝까지 처리해야 하므로 CancellationToken을 받지 않습니다.
/// </remarks>
public interface IPlayerGrainClient
{
    /// <summary>지정한 플레이어 Grain에 관리자 보상 명령을 전달합니다.</summary>
    /// <param name="playerId">보상을 받을 PlayerGrain의 Guid 기본 키입니다.</param>
    /// <param name="command">검증과 영속 처리를 요청할 관리자 보상 명령입니다.</param>
    Task<PlayerRewardCommandResult> GrantAdminRewardAsync(
        Guid playerId,
        GrantPlayerRewardCommand command);
}
