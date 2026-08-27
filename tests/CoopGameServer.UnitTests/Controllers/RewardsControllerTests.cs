using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Api.Controllers;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.GrainContracts.Players;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.UnitTests.Controllers;

/// <summary>
/// 보상 처리 경로를 PlayerGrain으로 바꿔도 기존 HTTP 상태 코드와 응답 본문이 유지되는지 검증합니다.
/// </summary>
public sealed class RewardsControllerTests
{
    [Fact]
    public async Task GrantRewardReturnsCreatedAndPreservesReceiptForNewReward()
    {
        var receipt = CreateReceipt();
        var controller = CreateController(Applied(receipt, isReplay: false));
        var request = CreateRequest(receipt);

        var actionResult = await controller.GrantReward(
            receipt.PlayerId,
            request,
            CancellationToken.None);

        var createdResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status201Created, createdResult.StatusCode);

        var response = Assert.IsType<GrantRewardResponse>(createdResult.Value);
        Assert.Equal(receipt.RewardAuditId, response.RewardAuditId);
        Assert.Equal(receipt.RequestId, response.RequestId);
        Assert.Equal(receipt.PlayerId, response.PlayerId);
        Assert.Equal(receipt.GoldAmount, response.GoldAmount);
        Assert.Equal(receipt.ItemId, response.ItemId);
        Assert.Equal(receipt.ItemQuantity, response.ItemQuantity);
        Assert.Equal(receipt.Reason, response.Reason);
        Assert.Equal(receipt.CreatedAt, response.CreatedAt);
        Assert.False(response.IsReplay);
    }

    [Fact]
    public async Task GrantRewardReturnsOkForIdempotentReplay()
    {
        var receipt = CreateReceipt();
        var controller = CreateController(Applied(receipt, isReplay: true));

        var actionResult = await controller.GrantReward(
            receipt.PlayerId,
            CreateRequest(receipt),
            CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<GrantRewardResponse>(okResult.Value);
        Assert.True(response.IsReplay);
        Assert.Equal(receipt.RewardAuditId, response.RewardAuditId);
    }

    [Fact]
    public async Task GrantRewardReturnsNotFoundWhenPlayerDoesNotExist()
    {
        var receipt = CreateReceipt();
        var controller = CreateController(Rejected(PlayerRewardCommandError.PlayerNotFound));

        var actionResult = await controller.GrantReward(
            receipt.PlayerId,
            CreateRequest(receipt),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult.Result);
    }

    [Fact]
    public async Task GrantRewardReturnsConflictWhenIdempotencyKeyHasDifferentPayload()
    {
        var receipt = CreateReceipt();
        var controller = CreateController(Rejected(PlayerRewardCommandError.IdempotencyConflict));

        var actionResult = await controller.GrantReward(
            receipt.PlayerId,
            CreateRequest(receipt),
            CancellationToken.None);

        var conflictResult = Assert.IsType<ConflictObjectResult>(actionResult.Result);
        var problemDetails = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
    }

    [Fact]
    public async Task GrantRewardReturnsBadRequestWhenPlayerGrainRejectsInvalidReward()
    {
        var receipt = CreateReceipt();
        var controller = CreateController(Rejected(PlayerRewardCommandError.InvalidRequest));

        var actionResult = await controller.GrantReward(
            receipt.PlayerId,
            CreateRequest(receipt),
            CancellationToken.None);

        var badRequestResult = Assert.IsAssignableFrom<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequestResult.StatusCode);
    }

    /// <summary>테스트마다 서로 충돌하지 않는 보상 영수증을 만듭니다.</summary>
    private static PlayerRewardReceipt CreateReceipt()
    {
        return new PlayerRewardReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            500,
            1001,
            2,
            "controller-regression-test",
            DateTimeOffset.UtcNow);
    }

    /// <summary>영수증과 같은 값을 가진 기존 HTTP 요청 형식을 만듭니다.</summary>
    private static GrantRewardRequest CreateRequest(PlayerRewardReceipt receipt)
    {
        return new GrantRewardRequest(
            receipt.RequestId,
            receipt.GoldAmount,
            receipt.ItemId,
            receipt.ItemQuantity,
            receipt.Reason);
    }

    /// <summary>지정한 PlayerGrain 결과를 반환하는 Controller를 만듭니다.</summary>
    private static RewardsController CreateController(PlayerRewardCommandResult grainResult)
    {
        var grainClient = new StubPlayerGrainClient(
            (_, _) => Task.FromResult(grainResult));
        return new RewardsController(new RewardService(grainClient));
    }

    private static PlayerRewardCommandResult Applied(
        PlayerRewardReceipt receipt,
        bool isReplay)
    {
        return new PlayerRewardCommandResult(
            isReplay,
            PlayerRewardCommandStatus.Applied,
            PlayerRewardCommandError.None,
            receipt);
    }

    private static PlayerRewardCommandResult Rejected(PlayerRewardCommandError error)
    {
        return new PlayerRewardCommandResult(
            IsReplay: false,
            PlayerRewardCommandStatus.Rejected,
            error,
            Receipt: null);
    }

    /// <summary>Controller가 받은 Grain 결과만 검증하도록 Orleans 호출을 대신하는 테스트 대역입니다.</summary>
    private sealed class StubPlayerGrainClient(
        Func<Guid, GrantPlayerRewardCommand, Task<PlayerRewardCommandResult>> grantHandler)
        : IPlayerGrainClient
    {
        /// <inheritdoc />
        public Task<PlayerRewardCommandResult> GrantAdminRewardAsync(
            Guid playerId,
            GrantPlayerRewardCommand command)
        {
            return grantHandler(playerId, command);
        }
    }
}
