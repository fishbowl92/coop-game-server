using System.Security.Claims;
using CoopGameServer.Api.Application.Parties;
using CoopGameServer.Api.Controllers;
using CoopGameServer.Contracts.Parties;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.IntegrationTests.Controllers;

/// <summary>
/// 실제 Orleans TestCluster와 PostgreSQL을 사용해 파티 HTTP 상태 코드와 응답 계약을 검증합니다.
/// </summary>
/// <remarks>
/// Controller 메서드를 직접 호출하므로 네트워크 포트는 열지 않지만,
/// 내부 PartyService → Orleans Client → Silo → PartyGrain → PostgreSQL 경로는 실제로 실행합니다.
/// </remarks>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class PartiesControllerTests(OrleansTestClusterFixture fixture)
{
    [Fact]
    public async Task CreatePartyReturnsCreatedThenOkReplayWithSamePartyId()
    {
        var leaderPlayerId = Guid.NewGuid();
        var request = new CreatePartyRequest(Guid.NewGuid(), leaderPlayerId);
        await fixture.RegisterPlayersAsync(leaderPlayerId);
        await using var gameDbContext = fixture.CreateDbContext();
        var controller = CreateController(gameDbContext, leaderPlayerId);

        var firstAction = await controller.CreateParty(request, CancellationToken.None);
        var replayAction = await controller.CreateParty(request, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedAtActionResult>(firstAction.Result);
        var firstResponse = Assert.IsType<PartyResponse>(createdResult.Value);
        var okResult = Assert.IsType<OkObjectResult>(replayAction.Result);
        var replayResponse = Assert.IsType<PartyResponse>(okResult.Value);

        Assert.Equal(nameof(PartiesController.GetPartyById), createdResult.ActionName);
        Assert.False(firstResponse.IsReplay);
        Assert.True(replayResponse.IsReplay);
        Assert.Equal(firstResponse.PartyId, replayResponse.PartyId);
        Assert.Equal([leaderPlayerId], firstResponse.MemberPlayerIds);
    }

    [Fact]
    public async Task ConcurrentSameCreateRequestReturnsOnePartyIdentity()
    {
        var leaderPlayerId = Guid.NewGuid();
        var request = new CreatePartyRequest(Guid.NewGuid(), leaderPlayerId);
        await fixture.RegisterPlayersAsync(leaderPlayerId);
        await using var firstDbContext = fixture.CreateDbContext();
        await using var secondDbContext = fixture.CreateDbContext();
        var firstController = CreateController(firstDbContext, leaderPlayerId);
        var secondController = CreateController(secondDbContext, leaderPlayerId);

        var actions = await Task.WhenAll(
            firstController.CreateParty(request, CancellationToken.None),
            secondController.CreateParty(request, CancellationToken.None));
        var responses = actions.Select(GetSuccessResponse).ToArray();

        // API가 요청마다 새 partyId를 만들더라도 PostgreSQL request_id PK의 승자 ID 하나로 수렴해야 합니다.
        Assert.Single(responses.Select(response => response.PartyId).Distinct());
        Assert.Single(responses, response => !response.IsReplay);
        Assert.Single(responses, response => response.IsReplay);
    }

    [Fact]
    public async Task GetJoinAndLeaderLeaveReturnCurrentPartyState()
    {
        var leaderPlayerId = Guid.NewGuid();
        var memberPlayerId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(leaderPlayerId, memberPlayerId);
        await using var gameDbContext = fixture.CreateDbContext();
        var controller = CreateController(gameDbContext, leaderPlayerId);
        var createAction = await controller.CreateParty(
            new CreatePartyRequest(Guid.NewGuid(), leaderPlayerId),
            CancellationToken.None);
        var partyId = GetSuccessResponse(createAction).PartyId;

        SetCurrentPlayer(controller, memberPlayerId);
        var joinAction = await controller.JoinParty(
            partyId,
            new JoinPartyRequest(Guid.NewGuid(), memberPlayerId),
            CancellationToken.None);
        var getAction = await controller.GetPartyById(partyId, CancellationToken.None);
        SetCurrentPlayer(controller, leaderPlayerId);
        var leaveAction = await controller.LeaveParty(
            partyId,
            new LeavePartyRequest(Guid.NewGuid(), leaderPlayerId),
            CancellationToken.None);

        var joined = GetSuccessResponse(joinAction);
        var queried = GetSuccessResponse(getAction);
        var afterLeaderLeave = GetSuccessResponse(leaveAction);

        Assert.Equal([leaderPlayerId, memberPlayerId], joined.MemberPlayerIds);
        Assert.Equal(joined.MemberPlayerIds, queried.MemberPlayerIds);
        Assert.Equal(memberPlayerId, afterLeaderLeave.LeaderPlayerId);
        Assert.Equal([memberPlayerId], afterLeaderLeave.MemberPlayerIds);
    }

    [Fact]
    public async Task InvalidAndMissingResourcesMapToBadRequestAndNotFound()
    {
        await using var gameDbContext = fixture.CreateDbContext();
        var controller = CreateController(gameDbContext, Guid.NewGuid(), isAdministrator: true);

        var invalidAction = await controller.CreateParty(
            new CreatePartyRequest(Guid.Empty, Guid.NewGuid()),
            CancellationToken.None);
        var invalidPartyAction = await controller.JoinParty(
            Guid.Empty,
            new JoinPartyRequest(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);
        var missingPartyAction = await controller.GetPartyById(
            Guid.NewGuid(),
            CancellationToken.None);
        var missingPlayerAction = await controller.CreateParty(
            new CreatePartyRequest(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        AssertStatusCode(invalidAction, StatusCodes.Status400BadRequest);
        AssertStatusCode(invalidPartyAction, StatusCodes.Status400BadRequest);
        Assert.IsType<NotFoundResult>(missingPartyAction.Result);
        AssertStatusCode(missingPlayerAction, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task StateConflictAndLeaderRuleMapToConflictAndForbidden()
    {
        var leaderPlayerId = Guid.NewGuid();
        var memberPlayerId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(leaderPlayerId, memberPlayerId);
        await using var gameDbContext = fixture.CreateDbContext();
        var controller = CreateController(gameDbContext, leaderPlayerId);
        var createAction = await controller.CreateParty(
            new CreatePartyRequest(Guid.NewGuid(), leaderPlayerId),
            CancellationToken.None);
        var partyId = GetSuccessResponse(createAction).PartyId;
        SetCurrentPlayer(controller, memberPlayerId);
        await controller.JoinParty(
            partyId,
            new JoinPartyRequest(Guid.NewGuid(), memberPlayerId),
            CancellationToken.None);

        var duplicateJoinAction = await controller.JoinParty(
            partyId,
            new JoinPartyRequest(Guid.NewGuid(), memberPlayerId),
            CancellationToken.None);
        var forbiddenDisbandAction = await controller.DisbandParty(
            partyId,
            new DisbandPartyRequest(Guid.NewGuid(), memberPlayerId),
            CancellationToken.None);

        AssertStatusCode(duplicateJoinAction, StatusCodes.Status409Conflict);
        AssertStatusCode(forbiddenDisbandAction, StatusCodes.Status403Forbidden);
    }

    /// <summary>테스트 클러스터와 테스트 DB를 사용하는 Controller를 구성합니다.</summary>
    private PartiesController CreateController(
        GameDbContext gameDbContext,
        Guid playerId,
        bool isAdministrator = false)
    {
        var partyService = new PartyService(fixture.Cluster.GrainFactory, gameDbContext);
        var controller = new PartiesController(partyService);
        SetCurrentPlayer(controller, playerId, isAdministrator);
        return controller;
    }

    /// <summary>직접 호출하는 Controller 테스트에 JWT 검증 후의 현재 Player Claim을 넣습니다.</summary>
    private static void SetCurrentPlayer(
        PartiesController controller,
        Guid playerId,
        bool isAdministrator = false)
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

    /// <summary>200 또는 201의 ObjectResult에서 파티 응답 본문을 꺼냅니다.</summary>
    private static PartyResponse GetSuccessResponse(ActionResult<PartyResponse> action)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(action.Result);
        Assert.True(
            objectResult.StatusCode is StatusCodes.Status200OK or StatusCodes.Status201Created);
        return Assert.IsType<PartyResponse>(objectResult.Value);
    }

    /// <summary>업무 오류가 예상한 HTTP 상태 코드와 ProblemDetails를 반환하는지 확인합니다.</summary>
    private static void AssertStatusCode(
        ActionResult<PartyResponse> action,
        int expectedStatusCode)
    {
        var objectResult = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(expectedStatusCode, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(expectedStatusCode, problemDetails.Status);
    }
}
