using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.Grains.Matchmaking;

/// <summary>API 사전 검사 없이 서로 다른 Grain을 동시에 호출해 영속 참가 경계를 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class PlayerParticipationTests(OrleansTestClusterFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoloEnqueueRacingPartyCreateOrJoinHasOnlyOneWinner(bool join)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var player = Guid.NewGuid();
            var leader = Guid.NewGuid();
            await fixture.RegisterPlayersAsync(player, leader);
            var party = fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(Guid.NewGuid());
            if (join) Assert.Equal(PartyCommandError.None, (await party.CreateAsync(Guid.NewGuid(), leader)).Error);
            var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>($"participation-{Guid.NewGuid():N}");
            var soloTask = queue.EnqueueAsync(Solo(player));
            var partyTask = join ? party.JoinAsync(Guid.NewGuid(), player) : party.CreateAsync(Guid.NewGuid(), player);
            await Task.WhenAll(soloTask, partyTask);
            var soloResult = await soloTask;
            var partyResult = await partyTask;
            var soloSucceeded = soloResult.Error == MatchQueueCommandError.None;
            var partySucceeded = partyResult.Error == PartyCommandError.None;
            Assert.NotEqual(soloSucceeded, partySucceeded);
            Assert.Equal(soloSucceeded ? PartyCommandError.PlayerAlreadyInMatchmaking : PartyCommandError.None, partyResult.Error);
            Assert.Equal(partySucceeded ? MatchQueueCommandError.SoloPlayerAlreadyInParty : MatchQueueCommandError.None, soloResult.Error);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(partySucceeded, await db.PartyMembers.AnyAsync(x => x.PlayerId == player));
            Assert.Equal(soloSucceeded, await db.MatchQueueMembers.AnyAsync(x => x.PlayerId == player));
        }
    }

    [Fact]
    public async Task OldEnqueueReplaysAfterCancellationPartyJoinAndSiloRestart()
    {
        var player = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(player);
        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>($"participation-replay-{Guid.NewGuid():N}");
        var request = Solo(player);
        var first = await queue.EnqueueAsync(request);
        Assert.Equal(MatchQueueCommandError.None, first.Error);
        var ticketId = first.Ticket!.TicketId;
        await queue.CancelAsync(new CancelMatchQueueRequest(Guid.NewGuid(), ticketId, player));
        var party = fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(Guid.NewGuid());
        Assert.Equal(PartyCommandError.None, (await party.CreateAsync(Guid.NewGuid(), player)).Error);
        await fixture.RestartAllSilosAsync();

        var replay = await queue.EnqueueAsync(request);
        Assert.True(replay.IsReplay);
        Assert.Equal(MatchQueueCommandError.None, replay.Error);
        Assert.Equal(ticketId, replay.Ticket!.TicketId);
        Assert.Equal(MatchQueueTicketStatus.Cancelled, (await queue.GetTicketAsync(ticketId))!.Status);
        Assert.Equal(MatchQueueCommandError.SoloPlayerAlreadyInParty, (await queue.EnqueueAsync(Solo(player))).Error);
    }

    [Fact]
    public async Task ConcurrentSoloEnqueuesAcrossQueuesCannotOccupyTwoTickets()
    {
        var player = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(player);
        var queues = Enumerable.Range(0, 2).Select(_ => fixture.Cluster.GrainFactory
            .GetGrain<IMatchQueueGrain>($"participation-queues-{Guid.NewGuid():N}")).ToArray();
        var results = await Task.WhenAll(queues.Select(queue => queue.EnqueueAsync(Solo(player))));
        Assert.Single(results, x => x.Error == MatchQueueCommandError.None);
        Assert.Single(results, x => x.Error == MatchQueueCommandError.PlayerAlreadyQueued);
    }

    private static MatchQueueEntryRequest Solo(Guid player) =>
        new(Guid.NewGuid(), MatchQueueEntryKind.SoloPlayer, null, player, [player]);
}
