using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.Grains.GameRooms;

namespace CoopGameServer.UnitTests.Domain.GameRooms;

public sealed class GameRoomDeadlineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void ReadyDeadlineCancelsExactlyAtBoundaryWithoutStarting()
    {
        var state = Create();
        Assert.False(state.EvaluateDeadlines(Now.AddSeconds(30).AddTicks(-1)));
        Assert.True(state.EvaluateDeadlines(Now.AddSeconds(30)));
        Assert.Equal(GameOutcome.Cancelled, state.Get()!.Outcome);
        Assert.Null(state.Get()!.StartedAt);
        Assert.Equal("InitialConnectionTimeout", state.Get()!.CancellationReason);
        var version = state.Get()!.StateVersion;
        Assert.False(state.EvaluateDeadlines(Now.AddHours(1)));
        Assert.Equal(version, state.Get()!.StateVersion);
    }

    [Fact]
    public void StartedRoomIgnoresInitialDeadlineButAbandonmentCausesDefeat()
    {
        var state = Create();
        foreach (var id in state.Get()!.PlayerIds)
            state.ExecuteConnection(new(Guid.NewGuid(), id, GameRoomConnectionAction.Connect), Now, Guid.NewGuid());
        state.Start(Guid.NewGuid(), Now);
        Assert.True(state.EvaluateDeadlines(Now.AddSeconds(30)));
        Assert.Equal(GameRoomLifecycle.InGame, state.Get()!.Lifecycle);
        Assert.All(state.Get()!.Players!, player => Assert.Equal(100, player.CurrentHealth));
        Assert.False(state.EvaluateDeadlines(Now.AddSeconds(45).AddTicks(-1)));
        Assert.True(state.EvaluateDeadlines(Now.AddSeconds(45)));
        Assert.Equal(GameOutcome.Defeat, state.Get()!.Outcome);
        Assert.All(state.Get()!.Players!, player =>
        {
            Assert.Equal(0, player.CurrentHealth);
            Assert.Equal("Abandoned", player.ConnectionStatus);
            Assert.Null(state.GetConnection(player.PlayerId).ConnectionId);
        });
    }

    private static GameRoomState Create()
    {
        var roomId = Guid.NewGuid();
        var state = new GameRoomState();
        state.Create(roomId, Guid.NewGuid(), new MatchAssignment(roomId, "coop-dungeon-normal-v1", [],
            Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(), Now));
        return state;
    }
}
