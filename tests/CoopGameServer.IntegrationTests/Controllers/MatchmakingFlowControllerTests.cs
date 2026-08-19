using System.Security.Claims;
using CoopGameServer.Api.Application.GameRooms;
using CoopGameServer.Api.Application.Matchmaking;
using CoopGameServer.Api.Controllers;
using CoopGameServer.Contracts.GameRooms;
using CoopGameServer.Contracts.Matchmaking;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.GrainContracts.Parties;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.IntegrationTests.Controllers;

/// <summary>
/// 인증된 HTTP 경계에서 PartyGrain → MatchQueueGrain → GameRoomGrain → PostgreSQL 전체 흐름을 검증합니다.
/// </summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class MatchmakingFlowControllerTests(OrleansTestClusterFixture fixture)
{
    [Fact]
    public async Task ThreePlayerPartyAndSoloReturnToLobbyThenCanMatchAgain()
    {
        var queueKey = CreateQueueKey();
        var partyId = Guid.NewGuid();
        var partyPlayers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var soloPlayerId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync([.. partyPlayers, soloPlayerId]);
        await CreatePartyAsync(partyId, partyPlayers);

        await using var partyQueueDbContext = fixture.CreateDbContext();
        var partyController = CreateMatchmakingController(
            partyQueueDbContext,
            partyPlayers[0]);
        var partyAction = await partyController.EnqueueParty(
            queueKey,
            partyId,
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var partyResponse = GetMatchmakingResponse(partyAction);

        await using var soloQueueDbContext = fixture.CreateDbContext();
        var soloController = CreateMatchmakingController(soloQueueDbContext, soloPlayerId);
        var soloRequest = new EnqueueMatchRequest(Guid.NewGuid());
        var soloAction = await soloController.EnqueueSolo(
            queueKey,
            soloRequest,
            CancellationToken.None);
        var soloResponse = GetMatchmakingResponse(soloAction);
        var match = Assert.IsType<MatchAssignmentResponse>(soloResponse.Match);

        Assert.Null(partyResponse.Match);
        Assert.Equal("Matched", soloResponse.Ticket.Status);
        Assert.Equal(match.RoomId, soloResponse.Ticket.RoomId);
        Assert.Equal([partyId], match.PartyIds);
        Assert.Equal([.. partyPlayers, soloPlayerId], match.PlayerIds);

        // 같은 솔로 등록 요청 재전송은 새 방을 만들지 않고 최초 roomId를 재생해야 합니다.
        var replayAction = await soloController.EnqueueSolo(
            queueKey,
            soloRequest,
            CancellationToken.None);
        var replayResponse = GetMatchmakingResponse(replayAction);
        Assert.True(replayResponse.IsReplay);
        Assert.Equal(match.RoomId, replayResponse.Match?.RoomId);

        var roomController = CreateGameRoomsController(soloPlayerId);
        var roomAction = await roomController.Get(match.RoomId, CancellationToken.None);
        var readyRoom = GetGameRoomResponse(roomAction);
        Assert.Equal("Ready", readyRoom.Lifecycle);

        var adminController = CreateGameRoomsController(Guid.NewGuid(), isAdministrator: true);
        var startAction = await adminController.Start(
            match.RoomId,
            new GameRoomCommandRequest(Guid.NewGuid()),
            CancellationToken.None);
        var startedRoom = GetGameRoomResponse(startAction);
        var inGameParty = Assert.IsType<PartySnapshot>(
            await fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(partyId).GetAsync());

        Assert.Equal("InGame", startedRoom.Lifecycle);
        Assert.Equal(PartyLifecycle.InGame, inGameParty.Lifecycle);
        Assert.Equal(match.RoomId, inGameParty.CurrentRoomId);

        var completeRequest = new GameRoomCommandRequest(Guid.NewGuid());
        var completeAction = await adminController.Complete(
            match.RoomId,
            completeRequest,
            CancellationToken.None);
        var completedRoom = GetGameRoomResponse(completeAction);
        var returnedParty = Assert.IsType<PartySnapshot>(
            await fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(partyId).GetAsync());

        Assert.Equal("Completed", completedRoom.Lifecycle);
        Assert.Equal(PartyLifecycle.Active, returnedParty.Lifecycle);
        Assert.Null(returnedParty.CurrentRoomId);
        Assert.Equal(partyPlayers, returnedParty.MemberPlayerIds);

        var replayCompleteAction = await adminController.Complete(
            match.RoomId,
            completeRequest,
            CancellationToken.None);
        Assert.True(GetGameRoomResponse(replayCompleteAction).IsReplay);

        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchQueueGrain>(queueKey);
        var completedPartyTicket = Assert.IsType<MatchQueueTicket>(
            await queue.GetTicketAsync(partyResponse.Ticket.TicketId));
        var completedSoloTicket = Assert.IsType<MatchQueueTicket>(
            await queue.GetTicketAsync(soloResponse.Ticket.TicketId));

        Assert.Equal(MatchQueueTicketStatus.Completed, completedPartyTicket.Status);
        Assert.Equal(MatchQueueTicketStatus.Completed, completedSoloTicket.Status);

        // 첫 게임의 Matched 티켓이 Completed로 해제됐으므로 같은 파티와 솔로가 두 번째 방에 들어갈 수 있어야 합니다.
        var secondPartyAction = await partyController.EnqueueParty(
            queueKey,
            partyId,
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var secondPartyResponse = GetMatchmakingResponse(secondPartyAction);
        var secondSoloAction = await soloController.EnqueueSolo(
            queueKey,
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var secondSoloResponse = GetMatchmakingResponse(secondSoloAction);
        var secondMatch = Assert.IsType<MatchAssignmentResponse>(secondSoloResponse.Match);

        Assert.Null(secondPartyResponse.Match);
        Assert.NotEqual(match.RoomId, secondMatch.RoomId);
        Assert.Equal([.. partyPlayers, soloPlayerId], secondMatch.PlayerIds);
    }

    [Fact]
    public async Task CancellingWaitingPartyUnlocksItsMembership()
    {
        var queueKey = CreateQueueKey();
        var partyId = Guid.NewGuid();
        var players = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await fixture.RegisterPlayersAsync(players);
        await CreatePartyAsync(partyId, players);

        await using var gameDbContext = fixture.CreateDbContext();
        var controller = CreateMatchmakingController(gameDbContext, players[0]);
        var enqueueAction = await controller.EnqueueParty(
            queueKey,
            partyId,
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var queuedTicket = GetMatchmakingResponse(enqueueAction).Ticket;

        var cancelAction = await controller.Cancel(
            queueKey,
            queuedTicket.TicketId,
            new CancelMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var cancelled = GetMatchmakingResponse(cancelAction);
        var party = Assert.IsType<PartySnapshot>(
            await fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(partyId).GetAsync());

        Assert.Equal("Cancelled", cancelled.Ticket.Status);
        Assert.Equal(PartyLifecycle.Active, party.Lifecycle);
    }

    [Fact]
    public async Task PartyMemberCannotQueueAsSoloOrManageAsNonLeader()
    {
        var queueKey = CreateQueueKey();
        var partyId = Guid.NewGuid();
        var players = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await fixture.RegisterPlayersAsync(players);
        await CreatePartyAsync(partyId, players);

        await using var gameDbContext = fixture.CreateDbContext();
        var controller = CreateMatchmakingController(gameDbContext, players[1]);

        var soloAction = await controller.EnqueueSolo(
            queueKey,
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var partyAction = await controller.EnqueueParty(
            queueKey,
            partyId,
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);

        AssertStatusCode(soloAction, StatusCodes.Status409Conflict);
        AssertStatusCode(partyAction, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task InvalidQueueRequestAndMissingTicketUseClientErrorStatusCodes()
    {
        var playerId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(playerId);
        await using var gameDbContext = fixture.CreateDbContext();
        var controller = CreateMatchmakingController(gameDbContext, playerId);

        var invalidQueueAction = await controller.EnqueueSolo(
            "   ",
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var unsupportedQueueAction = await controller.EnqueueSolo(
            "coop-dungeon-hard-v1",
            new EnqueueMatchRequest(Guid.NewGuid()),
            CancellationToken.None);
        var invalidRequestAction = await controller.EnqueueSolo(
            CreateQueueKey(),
            new EnqueueMatchRequest(Guid.Empty),
            CancellationToken.None);
        var missingTicketAction = await controller.Cancel(
            CreateQueueKey(),
            Guid.NewGuid(),
            new CancelMatchRequest(Guid.NewGuid()),
            CancellationToken.None);

        AssertStatusCode(invalidQueueAction, StatusCodes.Status400BadRequest);
        AssertStatusCode(unsupportedQueueAction, StatusCodes.Status400BadRequest);
        AssertStatusCode(invalidRequestAction, StatusCodes.Status400BadRequest);
        AssertStatusCode(missingTicketAction, StatusCodes.Status404NotFound);
    }

    /// <summary>실제 Player 행이 존재하는 사전 구성 파티를 Grain 명령으로 준비합니다.</summary>
    private async Task CreatePartyAsync(Guid partyId, Guid[] playerIds)
    {
        var party = fixture.Cluster.GrainFactory.GetGrain<IPartyGrain>(partyId);
        var createResult = await party.CreateAsync(Guid.NewGuid(), playerIds[0]);
        Assert.Equal(PartyCommandError.None, createResult.Error);

        foreach (var playerId in playerIds.Skip(1))
        {
            var joinResult = await party.JoinAsync(Guid.NewGuid(), playerId);
            Assert.Equal(PartyCommandError.None, joinResult.Error);
        }
    }

    private MatchmakingController CreateMatchmakingController(
        Persistence.GameDbContext gameDbContext,
        Guid playerId,
        bool isAdministrator = false)
    {
        var service = new MatchmakingService(fixture.Cluster.GrainFactory, gameDbContext);
        var controller = new MatchmakingController(service);
        SetCurrentPlayer(controller, playerId, isAdministrator);
        return controller;
    }

    private GameRoomsController CreateGameRoomsController(
        Guid playerId,
        bool isAdministrator = false)
    {
        var service = new GameRoomService(fixture.Cluster.GrainFactory);
        var controller = new GameRoomsController(service);
        SetCurrentPlayer(controller, playerId, isAdministrator);
        return controller;
    }

    /// <summary>직접 호출하는 Controller에 JWT 검증 뒤와 같은 Player·Role Claim을 넣습니다.</summary>
    private static void SetCurrentPlayer(
        ControllerBase controller,
        Guid playerId,
        bool isAdministrator)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, playerId.ToString()),
        };

        if (isAdministrator)
        {
            claims.Add(new Claim(ClaimTypes.Role, AccountRole.Administrator.ToString()));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test-jwt")),
            },
        };
    }

    private static MatchmakingResponse GetMatchmakingResponse(
        ActionResult<MatchmakingResponse> action)
    {
        var okResult = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<MatchmakingResponse>(okResult.Value);
    }

    private static GameRoomResponse GetGameRoomResponse(ActionResult<GameRoomResponse> action)
    {
        var okResult = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<GameRoomResponse>(okResult.Value);
    }

    private static void AssertStatusCode(
        ActionResult<MatchmakingResponse> action,
        int expectedStatusCode)
    {
        var objectResult = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(expectedStatusCode, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(expectedStatusCode, problemDetails.Status);
    }

    private static string CreateQueueKey() => MatchmakingQueueKeys.CoopDungeonNormalV1;
}
