using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using Orleans.TestingHost;

namespace CoopGameServer.IntegrationTests.Grains.Matchmaking;

/// <summary>
/// MatchQueueGrain의 4인 조합, 파티 보존, 멱등성, 취소 및 동시 호출 규칙을 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class MatchQueueGrainTests(OrleansTestClusterFixture fixture)
{
    private readonly TestCluster _cluster = fixture.Cluster;

    [Fact]
    public async Task FourPlayerPartyIsMatchedWithoutBeingSplit()
    {
        var queue = GetQueue();
        var request = CreateEntry(memberCount: 4);

        var result = await queue.EnqueueAsync(request);

        Assert.Equal(MatchQueueCommandError.None, result.Error);
        var ticket = Assert.IsType<MatchQueueTicket>(result.Ticket);
        var match = Assert.IsType<MatchAssignment>(result.Match);
        Assert.Equal(MatchQueueTicketStatus.Matched, ticket.Status);
        Assert.Equal(ticket.RoomId, match.RoomId);
        Assert.Equal([request.PartyId], match.PartyIds);
        Assert.Equal(request.MemberPlayerIds, match.PlayerIds);
        Assert.Empty((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task ThreeAndOneAreMatchedWhileEarlierTwoPlayerPartyKeepsWaiting()
    {
        var queue = GetQueue();
        var threePlayerParty = CreateEntry(memberCount: 3);
        var twoPlayerParty = CreateEntry(memberCount: 2);
        var soloParty = CreateEntry(memberCount: 1);

        await queue.EnqueueAsync(threePlayerParty);
        await queue.EnqueueAsync(twoPlayerParty);
        var result = await queue.EnqueueAsync(soloParty);

        var match = Assert.IsType<MatchAssignment>(result.Match);
        Assert.Equal([threePlayerParty.PartyId, soloParty.PartyId], match.PartyIds);
        Assert.Equal(4, match.PlayerIds.Length);

        var snapshot = await queue.GetSnapshotAsync();
        var waitingTicket = Assert.Single(snapshot.QueuedTickets);
        Assert.Equal(twoPlayerParty.PartyId, waitingTicket.PartyId);
        Assert.Equal(MatchQueueTicketStatus.Queued, waitingTicket.Status);
    }

    [Fact]
    public async Task TwoTwoPlayerPartiesAreMatchedTogether()
    {
        var queue = GetQueue();
        var firstParty = CreateEntry(memberCount: 2);
        var secondParty = CreateEntry(memberCount: 2);

        var firstResult = await queue.EnqueueAsync(firstParty);
        var secondResult = await queue.EnqueueAsync(secondParty);

        Assert.Null(firstResult.Match);
        var match = Assert.IsType<MatchAssignment>(secondResult.Match);
        Assert.Equal([firstParty.PartyId, secondParty.PartyId], match.PartyIds);
        Assert.Equal(4, match.PlayerIds.Length);
    }

    [Fact]
    public async Task SameEnqueueRequestReturnsOriginalResultAsReplay()
    {
        var queue = GetQueue();
        var request = CreateEntry(memberCount: 2);

        var firstResult = await queue.EnqueueAsync(request);
        var replayResult = await queue.EnqueueAsync(request);

        Assert.False(firstResult.IsReplay);
        Assert.True(replayResult.IsReplay);
        Assert.Equal(MatchQueueCommandError.None, replayResult.Error);
        AssertTicketsEquivalent(
            Assert.IsType<MatchQueueTicket>(firstResult.Ticket),
            Assert.IsType<MatchQueueTicket>(replayResult.Ticket));
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task SameRequestIdWithDifferentContentReturnsConflict()
    {
        var queue = GetQueue();
        var requestId = Guid.NewGuid();
        var firstRequest = CreateEntry(memberCount: 1, requestId: requestId);
        var changedRequest = CreateEntry(memberCount: 2, requestId: requestId);

        await queue.EnqueueAsync(firstRequest);
        var conflictResult = await queue.EnqueueAsync(changedRequest);

        Assert.Equal(MatchQueueCommandError.RequestIdConflict, conflictResult.Error);
        Assert.False(conflictResult.IsReplay);
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task DifferentRequestCannotQueueSamePartyTwice()
    {
        var queue = GetQueue();
        var firstRequest = CreateEntry(memberCount: 2);
        var secondRequest = firstRequest with { RequestId = Guid.NewGuid() };

        await queue.EnqueueAsync(firstRequest);
        var duplicateResult = await queue.EnqueueAsync(secondRequest);

        Assert.Equal(MatchQueueCommandError.PartyAlreadyQueued, duplicateResult.Error);
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task PlayerCannotWaitInTwoDifferentParties()
    {
        var queue = GetQueue();
        var sharedPlayerId = Guid.NewGuid();
        var firstRequest = CreateEntry(memberCount: 2, memberPlayerIds: [sharedPlayerId, Guid.NewGuid()]);
        var secondRequest = CreateEntry(memberCount: 2, memberPlayerIds: [sharedPlayerId, Guid.NewGuid()]);

        await queue.EnqueueAsync(firstRequest);
        var overlapResult = await queue.EnqueueAsync(secondRequest);

        Assert.Equal(MatchQueueCommandError.PlayerAlreadyQueued, overlapResult.Error);
        var waitingTicket = Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
        Assert.Equal(firstRequest.PartyId, waitingTicket.PartyId);
    }

    [Fact]
    public async Task LeaderCanCancelWaitingTicketAndReplaySameRequest()
    {
        var queue = GetQueue();
        var entry = CreateEntry(memberCount: 2);
        var cancelRequest = new CancelMatchQueueRequest(
            Guid.NewGuid(),
            entry.PartyId,
            entry.LeaderPlayerId);
        await queue.EnqueueAsync(entry);

        var firstResult = await queue.CancelAsync(cancelRequest);
        var replayResult = await queue.CancelAsync(cancelRequest);

        Assert.Equal(MatchQueueCommandError.None, firstResult.Error);
        Assert.Equal(MatchQueueTicketStatus.Cancelled, firstResult.Ticket?.Status);
        Assert.True(replayResult.IsReplay);
        AssertTicketsEquivalent(
            Assert.IsType<MatchQueueTicket>(firstResult.Ticket),
            Assert.IsType<MatchQueueTicket>(replayResult.Ticket));
        Assert.Empty((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task NonLeaderCannotCancelWaitingTicket()
    {
        var queue = GetQueue();
        var entry = CreateEntry(memberCount: 2);
        await queue.EnqueueAsync(entry);

        var result = await queue.CancelAsync(new CancelMatchQueueRequest(
            Guid.NewGuid(),
            entry.PartyId,
            entry.MemberPlayerIds[1]));

        Assert.Equal(MatchQueueCommandError.OnlyLeaderCanCancel, result.Error);
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task ConcurrentCancelAndMatchProduceOnlyOneConsistentOutcome()
    {
        var queue = GetQueue();
        var threePlayerParty = CreateEntry(memberCount: 3);
        var soloParty = CreateEntry(memberCount: 1);
        await queue.EnqueueAsync(threePlayerParty);

        // Orleans는 두 호출을 같은 MatchQueueGrain 안에서 순차 처리하지만 어느 요청이 먼저 도착할지는 보장하지 않습니다.
        var cancelTask = queue.CancelAsync(new CancelMatchQueueRequest(
            Guid.NewGuid(),
            threePlayerParty.PartyId,
            threePlayerParty.LeaderPlayerId));
        var matchTask = queue.EnqueueAsync(soloParty);
        await Task.WhenAll(cancelTask, matchTask);

        var cancelResult = await cancelTask;
        var matchResult = await matchTask;
        var threePlayerTicket = Assert.IsType<MatchQueueTicket>(
            await queue.GetTicketAsync(threePlayerParty.PartyId));
        var soloTicket = Assert.IsType<MatchQueueTicket>(
            await queue.GetTicketAsync(soloParty.PartyId));

        if (threePlayerTicket.Status == MatchQueueTicketStatus.Cancelled)
        {
            Assert.Equal(MatchQueueCommandError.None, cancelResult.Error);
            Assert.Equal(MatchQueueTicketStatus.Queued, soloTicket.Status);
            Assert.Null(matchResult.Match);
        }
        else
        {
            Assert.Equal(MatchQueueTicketStatus.Matched, threePlayerTicket.Status);
            Assert.Equal(MatchQueueTicketStatus.Matched, soloTicket.Status);
            Assert.Equal(MatchQueueCommandError.TicketAlreadyMatched, cancelResult.Error);
            Assert.NotNull(matchResult.Match);
        }
    }

    [Fact]
    public async Task DifferentQueueKeysKeepIndependentWaitingLists()
    {
        var firstQueue = GetQueue();
        var secondQueue = GetQueue();
        var firstEntry = CreateEntry(memberCount: 2);
        var secondEntry = CreateEntry(memberCount: 2);

        await firstQueue.EnqueueAsync(firstEntry);
        await secondQueue.EnqueueAsync(secondEntry);

        Assert.Equal(firstEntry.PartyId, Assert.Single((await firstQueue.GetSnapshotAsync()).QueuedTickets).PartyId);
        Assert.Equal(secondEntry.PartyId, Assert.Single((await secondQueue.GetSnapshotAsync()).QueuedTickets).PartyId);
    }

    [Fact]
    public async Task InvalidMemberShapeAndMissingLeaderAreRejected()
    {
        var queue = GetQueue();
        var duplicatedPlayerId = Guid.NewGuid();
        var invalidMembers = new MatchQueueEntryRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            duplicatedPlayerId,
            [duplicatedPlayerId, duplicatedPlayerId]);
        var missingLeader = CreateEntry(memberCount: 2) with { LeaderPlayerId = Guid.NewGuid() };

        var invalidMembersResult = await queue.EnqueueAsync(invalidMembers);
        var missingLeaderResult = await queue.EnqueueAsync(missingLeader);

        Assert.Equal(MatchQueueCommandError.InvalidMembers, invalidMembersResult.Error);
        Assert.Equal(MatchQueueCommandError.LeaderNotMember, missingLeaderResult.Error);
        Assert.Empty((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    /// <summary>각 테스트가 다른 문자열 키의 MatchQueueGrain을 사용하도록 고유 키를 만듭니다.</summary>
    private IMatchQueueGrain GetQueue()
    {
        return _cluster.GrainFactory.GetGrain<IMatchQueueGrain>($"queue-{Guid.NewGuid():N}");
    }

    /// <summary>요청한 인원수만큼 고유 플레이어 ID를 가진 파티 대기 요청을 만듭니다.</summary>
    private static MatchQueueEntryRequest CreateEntry(
        int memberCount,
        Guid? requestId = null,
        Guid[]? memberPlayerIds = null)
    {
        var members = memberPlayerIds
            ?? Enumerable.Range(0, memberCount).Select(_ => Guid.NewGuid()).ToArray();

        return new MatchQueueEntryRequest(
            requestId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            members[0],
            members);
    }

    /// <summary>
    /// 배열 참조가 아니라 티켓의 실제 값과 멤버 순서가 같은지 비교합니다.
    /// 방어적 복사 때문에 두 MemberPlayerIds 배열은 내용이 같아도 서로 다른 객체여야 합니다.
    /// </summary>
    private static void AssertTicketsEquivalent(
        MatchQueueTicket expected,
        MatchQueueTicket actual)
    {
        Assert.Equal(expected.TicketId, actual.TicketId);
        Assert.Equal(expected.QueueKey, actual.QueueKey);
        Assert.Equal(expected.PartyId, actual.PartyId);
        Assert.Equal(expected.LeaderPlayerId, actual.LeaderPlayerId);
        Assert.Equal(expected.MemberPlayerIds, actual.MemberPlayerIds);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RoomId, actual.RoomId);
        Assert.Equal(expected.EnqueuedAt, actual.EnqueuedAt);
    }
}
