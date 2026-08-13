using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Orleans.TestingHost;

namespace CoopGameServer.IntegrationTests.Grains.Parties;

/// <summary>
/// PartyGrain의 파티 규칙과 Orleans 동시 호출 처리를 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class PartyGrainTests(OrleansTestClusterFixture fixture)
{
    private readonly OrleansTestClusterFixture _fixture = fixture;
    private readonly TestCluster _cluster = fixture.Cluster;

    [Fact]
    public async Task CreateAsyncCreatesActivePartyWithLeaderAsFirstMember()
    {
        var partyId = Guid.NewGuid();
        var leaderPlayerId = Guid.NewGuid();
        var party = GetParty(partyId);
        await _fixture.RegisterPlayersAsync(leaderPlayerId);

        var result = await party.CreateAsync(Guid.NewGuid(), leaderPlayerId);

        Assert.Equal(PartyCommandError.None, result.Error);
        Assert.False(result.IsReplay);

        var snapshot = Assert.IsType<PartySnapshot>(result.Party);
        Assert.Equal(partyId, snapshot.PartyId);
        Assert.Equal(PartyLifecycle.Active, snapshot.Lifecycle);
        Assert.Equal(leaderPlayerId, snapshot.LeaderPlayerId);
        Assert.Equal([leaderPlayerId], snapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task SameRequestReturnsFirstResultAsReplay()
    {
        var leaderPlayerId = Guid.NewGuid();
        var memberPlayerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var party = GetParty(Guid.NewGuid());
        await _fixture.RegisterPlayersAsync(leaderPlayerId, memberPlayerId);

        var firstResult = await party.CreateAsync(requestId, leaderPlayerId);
        await party.JoinAsync(Guid.NewGuid(), memberPlayerId);

        var replayResult = await party.CreateAsync(requestId, leaderPlayerId);

        Assert.Equal(PartyCommandError.None, replayResult.Error);
        Assert.True(replayResult.IsReplay);

        // 재시도 응답은 현재 상태가 아니라 최초 명령 직후의 멤버 한 명 상태를 그대로 재사용합니다.
        var firstSnapshot = Assert.IsType<PartySnapshot>(firstResult.Party);
        var replaySnapshot = Assert.IsType<PartySnapshot>(replayResult.Party);
        Assert.Equal(firstSnapshot.MemberPlayerIds, replaySnapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task SameRequestIdWithDifferentContentReturnsConflict()
    {
        var requestId = Guid.NewGuid();
        var party = GetParty(Guid.NewGuid());
        var leaderPlayerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(leaderPlayerId);
        await party.CreateAsync(requestId, leaderPlayerId);

        var conflictResult = await party.JoinAsync(requestId, Guid.NewGuid());

        Assert.Equal(PartyCommandError.RequestIdConflict, conflictResult.Error);
        Assert.False(conflictResult.IsReplay);
    }

    [Fact]
    public async Task JoinAsyncRejectsDuplicateMemberAndFifthMember()
    {
        var leaderPlayerId = Guid.NewGuid();
        var party = await CreatePartyAsync(leaderPlayerId);
        var secondPlayerId = Guid.NewGuid();
        var thirdPlayerId = Guid.NewGuid();
        var fourthPlayerId = Guid.NewGuid();
        var fifthPlayerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(
            secondPlayerId,
            thirdPlayerId,
            fourthPlayerId,
            fifthPlayerId);

        var firstJoin = await party.JoinAsync(Guid.NewGuid(), secondPlayerId);
        var duplicateJoin = await party.JoinAsync(Guid.NewGuid(), secondPlayerId);
        await party.JoinAsync(Guid.NewGuid(), thirdPlayerId);
        await party.JoinAsync(Guid.NewGuid(), fourthPlayerId);
        var fullJoin = await party.JoinAsync(Guid.NewGuid(), fifthPlayerId);

        Assert.Equal(PartyCommandError.None, firstJoin.Error);
        Assert.Equal(PartyCommandError.MemberAlreadyJoined, duplicateJoin.Error);
        Assert.Equal(PartyCommandError.PartyFull, fullJoin.Error);

        var snapshot = Assert.IsType<PartySnapshot>(await party.GetAsync());
        Assert.Equal(4, snapshot.MemberPlayerIds.Length);
    }

    [Fact]
    public async Task LeaveAsyncRemovesOrdinaryMemberAndRejectsNonMember()
    {
        var leaderPlayerId = Guid.NewGuid();
        var memberPlayerId = Guid.NewGuid();
        var party = await CreatePartyAsync(leaderPlayerId);
        await _fixture.RegisterPlayersAsync(memberPlayerId);
        await party.JoinAsync(Guid.NewGuid(), memberPlayerId);

        var nonMemberResult = await party.LeaveAsync(Guid.NewGuid(), Guid.NewGuid());
        var leaveResult = await party.LeaveAsync(Guid.NewGuid(), memberPlayerId);

        Assert.Equal(PartyCommandError.MemberNotFound, nonMemberResult.Error);
        Assert.Equal(PartyCommandError.None, leaveResult.Error);

        var snapshot = Assert.IsType<PartySnapshot>(leaveResult.Party);
        Assert.Equal(leaderPlayerId, snapshot.LeaderPlayerId);
        Assert.Equal([leaderPlayerId], snapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task LeaderLeavePromotesFirstRemainingMemberInJoinOrder()
    {
        var leaderPlayerId = Guid.NewGuid();
        var firstMemberPlayerId = Guid.NewGuid();
        var secondMemberPlayerId = Guid.NewGuid();
        var party = await CreatePartyAsync(leaderPlayerId);
        await _fixture.RegisterPlayersAsync(firstMemberPlayerId, secondMemberPlayerId);
        await party.JoinAsync(Guid.NewGuid(), firstMemberPlayerId);
        await party.JoinAsync(Guid.NewGuid(), secondMemberPlayerId);

        var leaveResult = await party.LeaveAsync(Guid.NewGuid(), leaderPlayerId);

        var snapshot = Assert.IsType<PartySnapshot>(leaveResult.Party);
        Assert.Equal(firstMemberPlayerId, snapshot.LeaderPlayerId);
        Assert.Equal([firstMemberPlayerId, secondMemberPlayerId], snapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task LastMemberLeaveDisbandsParty()
    {
        var leaderPlayerId = Guid.NewGuid();
        var party = await CreatePartyAsync(leaderPlayerId);

        var leaveResult = await party.LeaveAsync(Guid.NewGuid(), leaderPlayerId);

        var snapshot = Assert.IsType<PartySnapshot>(leaveResult.Party);
        Assert.Equal(PartyLifecycle.Disbanded, snapshot.Lifecycle);
        Assert.Null(snapshot.LeaderPlayerId);
        Assert.Empty(snapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task DisbandAsyncAllowsOnlyCurrentLeaderAndDisbandsParty()
    {
        var leaderPlayerId = Guid.NewGuid();
        var memberPlayerId = Guid.NewGuid();
        var party = await CreatePartyAsync(leaderPlayerId);
        await _fixture.RegisterPlayersAsync(memberPlayerId);
        await party.JoinAsync(Guid.NewGuid(), memberPlayerId);

        var memberResult = await party.DisbandAsync(Guid.NewGuid(), memberPlayerId);
        var leaderResult = await party.DisbandAsync(Guid.NewGuid(), leaderPlayerId);

        Assert.Equal(PartyCommandError.OnlyLeaderCanDisband, memberResult.Error);
        Assert.Equal(PartyCommandError.None, leaderResult.Error);

        var snapshot = Assert.IsType<PartySnapshot>(leaderResult.Party);
        Assert.Equal(PartyLifecycle.Disbanded, snapshot.Lifecycle);
        Assert.Null(snapshot.LeaderPlayerId);
        Assert.Empty(snapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task DisbandedPartyIdCannotBeCreatedAgain()
    {
        var leaderPlayerId = Guid.NewGuid();
        var party = await CreatePartyAsync(leaderPlayerId);
        await party.DisbandAsync(Guid.NewGuid(), leaderPlayerId);

        var recreateResult = await party.CreateAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(PartyCommandError.PartyIdCannotBeReused, recreateResult.Error);
        var snapshot = Assert.IsType<PartySnapshot>(recreateResult.Party);
        Assert.Equal(PartyLifecycle.Disbanded, snapshot.Lifecycle);
    }

    [Fact]
    public async Task ConcurrentJoinsNeverExceedFourMembers()
    {
        var party = await CreatePartyAsync(Guid.NewGuid());
        var joiningPlayerIds = Enumerable.Range(0, 10)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        await _fixture.RegisterPlayersAsync(joiningPlayerIds);

        var joinTasks = joiningPlayerIds
            .Select(playerId => party.JoinAsync(Guid.NewGuid(), playerId))
            .ToArray();

        var results = await Task.WhenAll(joinTasks);

        Assert.Equal(3, results.Count(result => result.Error == PartyCommandError.None));
        Assert.Equal(7, results.Count(result => result.Error == PartyCommandError.PartyFull));

        var snapshot = Assert.IsType<PartySnapshot>(await party.GetAsync());
        Assert.Equal(4, snapshot.MemberPlayerIds.Length);
        Assert.Equal(4, snapshot.MemberPlayerIds.Distinct().Count());
    }

    [Fact]
    public async Task DifferentPartyIdsKeepIndependentState()
    {
        var firstPartyId = Guid.NewGuid();
        var secondPartyId = Guid.NewGuid();
        var firstLeaderPlayerId = Guid.NewGuid();
        var secondLeaderPlayerId = Guid.NewGuid();
        var firstMemberPlayerId = Guid.NewGuid();
        var firstParty = GetParty(firstPartyId);
        var secondParty = GetParty(secondPartyId);
        await _fixture.RegisterPlayersAsync(
            firstLeaderPlayerId,
            secondLeaderPlayerId,
            firstMemberPlayerId);

        await firstParty.CreateAsync(Guid.NewGuid(), firstLeaderPlayerId);
        await secondParty.CreateAsync(Guid.NewGuid(), secondLeaderPlayerId);
        await firstParty.JoinAsync(Guid.NewGuid(), firstMemberPlayerId);

        var firstSnapshot = Assert.IsType<PartySnapshot>(await firstParty.GetAsync());
        var secondSnapshot = Assert.IsType<PartySnapshot>(await secondParty.GetAsync());

        Assert.Equal(firstPartyId, firstSnapshot.PartyId);
        Assert.Equal(secondPartyId, secondSnapshot.PartyId);
        Assert.Equal(2, firstSnapshot.MemberPlayerIds.Length);
        Assert.Equal([secondLeaderPlayerId], secondSnapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task GetAsyncReturnsCopyWithoutChangingPartyState()
    {
        var leaderPlayerId = Guid.NewGuid();
        var party = GetParty(Guid.NewGuid());
        await _fixture.RegisterPlayersAsync(leaderPlayerId);

        Assert.Null(await party.GetAsync());
        await party.CreateAsync(Guid.NewGuid(), leaderPlayerId);

        var firstSnapshot = Assert.IsType<PartySnapshot>(await party.GetAsync());
        firstSnapshot.MemberPlayerIds[0] = Guid.NewGuid();
        var secondSnapshot = Assert.IsType<PartySnapshot>(await party.GetAsync());

        Assert.Equal(leaderPlayerId, secondSnapshot.LeaderPlayerId);
        Assert.Equal([leaderPlayerId], secondSnapshot.MemberPlayerIds);
    }

    [Fact]
    public async Task StateAndRequestReplaySurviveSiloRestart()
    {
        var partyId = Guid.NewGuid();
        var leaderPlayerId = Guid.NewGuid();
        var memberPlayerId = Guid.NewGuid();
        var createRequestId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(leaderPlayerId, memberPlayerId);

        var party = GetParty(partyId);
        var firstCreateResult = await party.CreateAsync(createRequestId, leaderPlayerId);
        await party.JoinAsync(Guid.NewGuid(), memberPlayerId);

        // 테스트 Silo를 실제로 재시작해 모든 PartyGrain 메모리를 폐기합니다.
        // 다음 호출에서 Grain이 다시 활성화되며 OnActivateAsync가 PostgreSQL 상태를 복원해야 합니다.
        await _cluster.RestartSiloAsync(_cluster.Primary);

        var restoredParty = GetParty(partyId);
        var restoredSnapshot = Assert.IsType<PartySnapshot>(await restoredParty.GetAsync());
        var replayResult = await restoredParty.CreateAsync(createRequestId, leaderPlayerId);

        Assert.Equal([leaderPlayerId, memberPlayerId], restoredSnapshot.MemberPlayerIds);
        Assert.True(replayResult.IsReplay);
        Assert.Equal(firstCreateResult.Error, replayResult.Error);
        Assert.Equal(
            Assert.IsType<PartySnapshot>(firstCreateResult.Party).MemberPlayerIds,
            Assert.IsType<PartySnapshot>(replayResult.Party).MemberPlayerIds);
    }

    [Fact]
    public async Task SamePlayerCannotJoinTwoActiveParties()
    {
        var firstLeaderPlayerId = Guid.NewGuid();
        var secondLeaderPlayerId = Guid.NewGuid();
        var sharedPlayerId = Guid.NewGuid();
        await _fixture.RegisterPlayersAsync(
            firstLeaderPlayerId,
            secondLeaderPlayerId,
            sharedPlayerId);

        var firstParty = await CreatePartyAsync(firstLeaderPlayerId);
        var secondParty = await CreatePartyAsync(secondLeaderPlayerId);

        var results = await Task.WhenAll(
            firstParty.JoinAsync(Guid.NewGuid(), sharedPlayerId),
            secondParty.JoinAsync(Guid.NewGuid(), sharedPlayerId));

        Assert.Single(results, result => result.Error == PartyCommandError.None);
        Assert.Single(
            results,
            result => result.Error == PartyCommandError.PlayerAlreadyInAnotherParty);

        await using var gameDbContext = _fixture.CreateDbContext();
        Assert.Equal(
            1,
            await gameDbContext.PartyMembers.CountAsync(member => member.PlayerId == sharedPlayerId));
    }

    [Fact]
    public async Task CreateAsyncRejectsUnknownPlayerAndReplaysFailure()
    {
        var requestId = Guid.NewGuid();
        var unknownPlayerId = Guid.NewGuid();
        var party = GetParty(Guid.NewGuid());

        var firstResult = await party.CreateAsync(requestId, unknownPlayerId);
        var replayResult = await party.CreateAsync(requestId, unknownPlayerId);

        Assert.Equal(PartyCommandError.PlayerNotFound, firstResult.Error);
        Assert.False(firstResult.IsReplay);
        Assert.Equal(PartyCommandError.PlayerNotFound, replayResult.Error);
        Assert.True(replayResult.IsReplay);
        Assert.Null(await party.GetAsync());
    }

    /// <summary>
    /// 고유 partyId로 Grain 참조를 얻고 생성까지 마친 상태를 반환합니다.
    /// </summary>
    private async Task<IPartyGrain> CreatePartyAsync(Guid leaderPlayerId)
    {
        await _fixture.RegisterPlayersAsync(leaderPlayerId);
        var party = GetParty(Guid.NewGuid());
        var result = await party.CreateAsync(Guid.NewGuid(), leaderPlayerId);
        Assert.Equal(PartyCommandError.None, result.Error);
        return party;
    }

    /// <summary>
    /// 객체를 직접 생성하지 않고 Orleans Client를 통해 Guid 키의 PartyGrain 참조를 얻습니다.
    /// </summary>
    private IPartyGrain GetParty(Guid partyId)
    {
        return _cluster.GrainFactory.GetGrain<IPartyGrain>(partyId);
    }
}
