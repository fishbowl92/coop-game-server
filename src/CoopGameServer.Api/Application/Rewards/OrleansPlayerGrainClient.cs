using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>IGrainFactory를 사용해 PlayerGrain Proxy를 얻고 보상 명령을 전달합니다.</summary>
public sealed class OrleansPlayerGrainClient : IPlayerGrainClient
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>API 호스트에 연결된 Orleans Grain Factory를 주입받습니다.</summary>
    /// <param name="grainFactory">Guid Player ID로 PlayerGrain Proxy를 만드는 Factory입니다.</param>
    public OrleansPlayerGrainClient(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public Task<PlayerRewardCommandResult> GrantAdminRewardAsync(
        Guid playerId,
        GrantPlayerRewardCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Proxy는 로컬 객체처럼 보이지만 실제 호출은 API 프로세스에서 Silo의 PlayerGrain으로 전달됩니다.
        var playerGrain = _grainFactory.GetGrain<IPlayerGrain>(playerId);
        return playerGrain.GrantAdminRewardAsync(command);
    }
}
