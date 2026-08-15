using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Orleans.TestingHost;

namespace CoopGameServer.IntegrationTests.Grains.Matchmaking;

/// <summary>
/// MatchQueueGrain의 4인 조합, 사전 구성 파티·솔로 구분, 영속성, 멱등성과 동시 호출 규칙을 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class MatchQueueGrainTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;
    private readonly TestCluster _cluster = fixture.Cluster;

    [Fact]
    public async Task FourPlayerPreformedPartyIsMatchedWithoutBeingSplit()
    {
        var queue = GetQueue();
        var request = CreatePreformedEntry(memberCount: 4);

        var result = await queue.EnqueueAsync(request);

        Assert.Equal(MatchQueueCommandError.None, result.Error);
        var ticket = Assert.IsType<MatchQueueTicket>(result.Ticket);
        var match = Assert.IsType<MatchAssignment>(result.Match);
        Assert.Equal(MatchQueueEntryKind.PreformedParty, ticket.EntryKind);
        Assert.Equal(MatchQueueTicketStatus.Matched, ticket.Status);
        Assert.Equal(ticket.RoomId, match.RoomId);
        Assert.Equal([request.PartyId!.Value], match.PartyIds);
        Assert.Equal(request.MemberPlayerIds, match.PlayerIds);
        Assert.Empty((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task ThreePlayerPartyAndSoloPlayerAreMatchedWithoutCreatingSoloParty()
    {
        var queue = GetQueue();
        var threePlayerParty = CreatePreformedEntry(memberCount: 3);
        var twoPlayerParty = CreatePreformedEntry(memberCount: 2);
        var soloPlayer = CreateSoloEntry();

        await queue.EnqueueAsync(threePlayerParty);
        await queue.EnqueueAsync(twoPlayerParty);
        var result = await queue.EnqueueAsync(soloPlayer);

        var match = Assert.IsType<MatchAssignment>(result.Match);
        Assert.Equal([threePlayerParty.PartyId!.Value], match.PartyIds);
        Assert.Equal(
            [.. threePlayerParty.MemberPlayerIds, .. soloPlayer.MemberPlayerIds],
            match.PlayerIds);
        Assert.Equal(4, match.PlayerIds.Length);

        var soloTicket = Assert.IsType<MatchQueueTicket>(result.Ticket);
        Assert.Equal(MatchQueueEntryKind.SoloPlayer, soloTicket.EntryKind);
        Assert.Null(soloTicket.PartyId);

        var waitingTicket = Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
        Assert.Equal(twoPlayerParty.PartyId, waitingTicket.PartyId);
    }

    [Fact]
    public async Task TwoTwoPlayerPreformedPartiesAreMatchedTogether()
    {
        var queue = GetQueue();
        var firstParty = CreatePreformedEntry(memberCount: 2);
        var secondParty = CreatePreformedEntry(memberCount: 2);

        var firstResult = await queue.EnqueueAsync(firstParty);
        var secondResult = await queue.EnqueueAsync(secondParty);

        Assert.Null(firstResult.Match);
        var match = Assert.IsType<MatchAssignment>(secondResult.Match);
        Assert.Equal([firstParty.PartyId!.Value, secondParty.PartyId!.Value], match.PartyIds);
        Assert.Equal(4, match.PlayerIds.Length);
    }

    [Fact]
    public async Task SameEnqueueRequestReturnsOriginalResultAsReplay()
    {
        var queue = GetQueue();
        var request = CreatePreformedEntry(memberCount: 2);

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
        var firstRequest = CreateSoloEntry(requestId: requestId);
        var changedRequest = CreatePreformedEntry(memberCount: 2, requestId: requestId);

        await queue.EnqueueAsync(firstRequest);
        var conflictResult = await queue.EnqueueAsync(changedRequest);

        Assert.Equal(MatchQueueCommandError.RequestIdConflict, conflictResult.Error);
        Assert.False(conflictResult.IsReplay);
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task DifferentRequestCannotQueueSamePreformedPartyTwice()
    {
        var queue = GetQueue();
        var firstRequest = CreatePreformedEntry(memberCount: 2);
        var secondRequest = firstRequest with { RequestId = Guid.NewGuid() };

        await queue.EnqueueAsync(firstRequest);
        var duplicateResult = await queue.EnqueueAsync(secondRequest);

        Assert.Equal(MatchQueueCommandError.PartyAlreadyQueued, duplicateResult.Error);
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task PlayerCannotWaitInTwoDifferentTickets()
    {
        var queue = GetQueue();
        var sharedPlayerId = Guid.NewGuid();
        var firstRequest = CreatePreformedEntry(
            memberCount: 2,
            memberPlayerIds: [sharedPlayerId, Guid.NewGuid()]);
        var secondRequest = CreatePreformedEntry(
            memberCount: 2,
            memberPlayerIds: [sharedPlayerId, Guid.NewGuid()]);

        await queue.EnqueueAsync(firstRequest);
        var overlapResult = await queue.EnqueueAsync(secondRequest);

        Assert.Equal(MatchQueueCommandError.PlayerAlreadyQueued, overlapResult.Error);
        Assert.Equal(firstRequest.PartyId, Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets).PartyId);
    }

    [Fact]
    public async Task SoloEntryRequiresExactlyOnePlayerAndNoPartyId()
    {
        var queue = GetQueue();
        var playerIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var multipleSoloPlayers = new MatchQueueEntryRequest(
            Guid.NewGuid(),
            MatchQueueEntryKind.SoloPlayer,
            PartyId: null,
            playerIds[0],
            playerIds);
        var soloWithPartyId = new MatchQueueEntryRequest(
            Guid.NewGuid(),
            MatchQueueEntryKind.SoloPlayer,
            Guid.NewGuid(),
            playerIds[0],
            [playerIds[0]]);

        var multipleResult = await queue.EnqueueAsync(multipleSoloPlayers);
        var partyIdResult = await queue.EnqueueAsync(soloWithPartyId);

        Assert.Equal(MatchQueueCommandError.InvalidEntryShape, multipleResult.Error);
        Assert.Equal(MatchQueueCommandError.InvalidEntryShape, partyIdResult.Error);
        Assert.Empty((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task TicketOwnerCanCancelWaitingTicketAndReplaySameRequest()
    {
        var queue = GetQueue();
        var entry = CreatePreformedEntry(memberCount: 2);
        var queuedResult = await queue.EnqueueAsync(entry);
        var queuedTicket = Assert.IsType<MatchQueueTicket>(queuedResult.Ticket);
        var cancelRequest = new CancelMatchQueueRequest(
            Guid.NewGuid(),
            queuedTicket.TicketId,
            entry.LeaderPlayerId);

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
    public async Task NonLeaderCannotCancelPreformedPartyTicket()
    {
        var queue = GetQueue();
        var entry = CreatePreformedEntry(memberCount: 2);
        var queuedTicket = Assert.IsType<MatchQueueTicket>((await queue.EnqueueAsync(entry)).Ticket);

        var result = await queue.CancelAsync(new CancelMatchQueueRequest(
            Guid.NewGuid(),
            queuedTicket.TicketId,
            entry.MemberPlayerIds[1]));

        Assert.Equal(MatchQueueCommandError.OnlyLeaderCanCancel, result.Error);
        Assert.Single((await queue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task ConcurrentCancelAndMatchProduceOnlyOneConsistentOutcome()
    {
        var queue = GetQueue();
        var threePlayerParty = CreatePreformedEntry(memberCount: 3);
        var soloPlayer = CreateSoloEntry();
        var threePlayerTicket = Assert.IsType<MatchQueueTicket>((await queue.EnqueueAsync(threePlayerParty)).Ticket);

        // Orleans는 두 호출을 같은 MatchQueueGrain 안에서 순차 처리하지만 어느 요청이 먼저 도착할지는 보장하지 않습니다.
        var cancelTask = queue.CancelAsync(new CancelMatchQueueRequest(
            Guid.NewGuid(),
            threePlayerTicket.TicketId,
            threePlayerParty.LeaderPlayerId));
        var matchTask = queue.EnqueueAsync(soloPlayer);
        await Task.WhenAll(cancelTask, matchTask);

        var cancelResult = await cancelTask;
        var matchResult = await matchTask;
        var finalThreePlayerTicket = Assert.IsType<MatchQueueTicket>(
            await queue.GetTicketAsync(threePlayerTicket.TicketId));
        var finalSoloTicket = Assert.IsType<MatchQueueTicket>(
            await queue.GetTicketAsync(Assert.IsType<MatchQueueTicket>(matchResult.Ticket).TicketId));

        if (finalThreePlayerTicket.Status == MatchQueueTicketStatus.Cancelled)
        {
            Assert.Equal(MatchQueueCommandError.None, cancelResult.Error);
            Assert.Equal(MatchQueueTicketStatus.Queued, finalSoloTicket.Status);
            Assert.Null(matchResult.Match);
        }
        else
        {
            Assert.Equal(MatchQueueTicketStatus.Matched, finalThreePlayerTicket.Status);
            Assert.Equal(MatchQueueTicketStatus.Matched, finalSoloTicket.Status);
            Assert.Equal(MatchQueueCommandError.TicketAlreadyMatched, cancelResult.Error);
            Assert.NotNull(matchResult.Match);
        }
    }

    [Fact]
    public async Task DifferentQueueKeysKeepIndependentWaitingLists()
    {
        var firstQueue = GetQueue();
        var secondQueue = GetQueue();
        var firstEntry = CreatePreformedEntry(memberCount: 2);
        var secondEntry = CreateSoloEntry();

        await firstQueue.EnqueueAsync(firstEntry);
        await secondQueue.EnqueueAsync(secondEntry);

        Assert.Equal(firstEntry.PartyId, Assert.Single((await firstQueue.GetSnapshotAsync()).QueuedTickets).PartyId);
        Assert.Null(Assert.Single((await secondQueue.GetSnapshotAsync()).QueuedTickets).PartyId);
    }

    [Fact]
    public async Task QueuedTicketAndIdempotencyResultSurviveSiloRestart()
    {
        var queueKey = CreateQueueKey();
        var queue = GetQueue(queueKey);
        var request = CreatePreformedEntry(memberCount: 2);
        var firstResult = await queue.EnqueueAsync(request);
        var firstTicket = Assert.IsType<MatchQueueTicket>(firstResult.Ticket);

        // 실제 테스트 Silo 재시작으로 모든 Grain 메모리를 폐기한 뒤 PostgreSQL 복원을 검증합니다.
        await _cluster.RestartSiloAsync(_cluster.Primary);

        var restoredQueue = GetQueue(queueKey);
        var restoredTicket = Assert.IsType<MatchQueueTicket>(await restoredQueue.GetTicketAsync(firstTicket.TicketId));
        var replayResult = await restoredQueue.EnqueueAsync(request);

        AssertTicketsEquivalent(firstTicket, restoredTicket);
        Assert.True(replayResult.IsReplay);
        AssertTicketsEquivalent(firstTicket, Assert.IsType<MatchQueueTicket>(replayResult.Ticket));
        Assert.Single((await restoredQueue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task MatchedRoomAssignmentSurvivesSiloRestart()
    {
        var queueKey = CreateQueueKey();
        var queue = GetQueue(queueKey);
        var party = CreatePreformedEntry(memberCount: 3);
        var soloPlayer = CreateSoloEntry();
        var partyResult = await queue.EnqueueAsync(party);
        var matchResult = await queue.EnqueueAsync(soloPlayer);
        var match = Assert.IsType<MatchAssignment>(matchResult.Match);
        var partyTicketId = Assert.IsType<MatchQueueTicket>(partyResult.Ticket).TicketId;
        var soloTicketId = Assert.IsType<MatchQueueTicket>(matchResult.Ticket).TicketId;

        await _cluster.RestartSiloAsync(_cluster.Primary);

        var restoredQueue = GetQueue(queueKey);
        var restoredPartyTicket = Assert.IsType<MatchQueueTicket>(await restoredQueue.GetTicketAsync(partyTicketId));
        var restoredSoloTicket = Assert.IsType<MatchQueueTicket>(await restoredQueue.GetTicketAsync(soloTicketId));

        Assert.Equal(MatchQueueTicketStatus.Matched, restoredPartyTicket.Status);
        Assert.Equal(MatchQueueTicketStatus.Matched, restoredSoloTicket.Status);
        Assert.Equal(match.RoomId, restoredPartyTicket.RoomId);
        Assert.Equal(match.RoomId, restoredSoloTicket.RoomId);
        Assert.Empty((await restoredQueue.GetSnapshotAsync()).QueuedTickets);
    }

    [Fact]
    public async Task SoloTicketIsPersistedWithoutPartyId()
    {
        var queueKey = CreateQueueKey();
        var queue = GetQueue(queueKey);
        var soloRequest = CreateSoloEntry();
        var queuedTicket = Assert.IsType<MatchQueueTicket>((await queue.EnqueueAsync(soloRequest)).Ticket);

        await using var gameDbContext = _fixture.CreateDbContext();
        var ticketRecord = await gameDbContext.MatchQueueTickets
            .SingleAsync(ticket => ticket.TicketId == queuedTicket.TicketId);
        var memberRecord = await gameDbContext.MatchQueueMembers
            .SingleAsync(member => member.TicketId == queuedTicket.TicketId);
        var requestRecord = await gameDbContext.MatchQueueRequests
            .SingleAsync(request => request.RequestId == soloRequest.RequestId);

        Assert.Equal(queueKey, ticketRecord.QueueKey);
        Assert.Equal((int)MatchQueueEntryKind.SoloPlayer, ticketRecord.EntryKind);
        Assert.Null(ticketRecord.PartyId);
        Assert.Equal(soloRequest.LeaderPlayerId, memberRecord.PlayerId);
        Assert.Contains("SoloPlayer", requestRecord.RequestPayloadJson, StringComparison.Ordinal);
        Assert.NotEmpty(requestRecord.ResultPayloadJson);
    }

    /// <summary>각 테스트가 다른 문자열 키의 MatchQueueGrain을 사용하도록 고유 키를 만듭니다.</summary>
    private IMatchQueueGrain GetQueue()
    {
        return GetQueue(CreateQueueKey());
    }

    /// <summary>주어진 문자열 키와 같은 Orleans MatchQueueGrain 참조를 얻습니다.</summary>
    private IMatchQueueGrain GetQueue(string queueKey)
    {
        return _cluster.GrainFactory.GetGrain<IMatchQueueGrain>(queueKey);
    }

    /// <summary>테스트 간 DB 대기열이 섞이지 않도록 고유한 매칭 조건 키를 만듭니다.</summary>
    private static string CreateQueueKey() => $"queue-{Guid.NewGuid():N}";

    /// <summary>요청한 인원수만큼 고유 플레이어 ID를 가진 사전 구성 파티 대기 요청을 만듭니다.</summary>
    private static MatchQueueEntryRequest CreatePreformedEntry(
        int memberCount,
        Guid? requestId = null,
        Guid[]? memberPlayerIds = null)
    {
        var members = memberPlayerIds
            ?? Enumerable.Range(0, memberCount).Select(_ => Guid.NewGuid()).ToArray();

        return new MatchQueueEntryRequest(
            requestId ?? Guid.NewGuid(),
            MatchQueueEntryKind.PreformedParty,
            Guid.NewGuid(),
            members[0],
            members);
    }

    /// <summary>인증된 플레이어 한 명만 포함하는 솔로 대기 요청을 만듭니다.</summary>
    private static MatchQueueEntryRequest CreateSoloEntry(
        Guid? requestId = null,
        Guid? playerId = null)
    {
        var soloPlayerId = playerId ?? Guid.NewGuid();

        return new MatchQueueEntryRequest(
            requestId ?? Guid.NewGuid(),
            MatchQueueEntryKind.SoloPlayer,
            PartyId: null,
            soloPlayerId,
            [soloPlayerId]);
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
        Assert.Equal(expected.EntryKind, actual.EntryKind);
        Assert.Equal(expected.PartyId, actual.PartyId);
        Assert.Equal(expected.LeaderPlayerId, actual.LeaderPlayerId);
        Assert.Equal(expected.MemberPlayerIds, actual.MemberPlayerIds);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RoomId, actual.RoomId);
        Assert.Equal(expected.EnqueuedAt, actual.EnqueuedAt);
        Assert.Equal(expected.QueueOrder, actual.QueueOrder);
    }
}
