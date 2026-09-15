using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.Api.Application.Rewards;

/// <summary>IGrainFactory를 사용해 PlayerGrain Proxy를 얻고 호출을 전달합니다.</summary>
public sealed class OrleansPlayerGrainClient : IPlayerGrainClient
{
    private readonly IGrainFactory _grainFactory;

    public OrleansPlayerGrainClient(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    public Task<PlayerRewardCommandResult> GrantAdminRewardAsync(
        Guid playerId,
        GrantPlayerRewardCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return GetPlayerGrain(playerId).GrantAdminRewardAsync(command);
    }

    public Task<PlayerProgressionPageResult> GetProgressionPageAsync(
        Guid playerId,
        GetPlayerProgressionPageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetPlayerGrain(playerId).GetProgressionPageAsync(query);
    }

    public Task InvalidateProgressionCacheAsync(Guid playerId) =>
        GetPlayerGrain(playerId).InvalidateProgressionCacheAsync();

    private IPlayerGrain GetPlayerGrain(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("Player ID must not be empty.", nameof(playerId));
        }

        // Proxy는 로컬 객체처럼 보이지만 실제 호출은 API 프로세스에서 Silo로 전달됩니다.
        return _grainFactory.GetGrain<IPlayerGrain>(playerId);
    }
}
