using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.UnitTests.Application.Rewards;

/// <summary>
/// HTTP 보상 계약을 PlayerGrain 명령으로 변환하는 API 어댑터를 검증합니다.
/// </summary>
/// <remarks>
/// 실제 Grain 실행·트랜잭션·동시성은 Orleans와 PostgreSQL 통합 테스트가 담당합니다.
/// 여기서는 입력 변환, 결과 전달, HTTP 응답 대기 취소 경계만 빠르게 검증합니다.
/// </remarks>
public sealed class RewardServiceTests
{
    [Fact]
    public async Task GrantAsyncMapsHttpRequestToPlayerGrainCommandAndReturnsResult()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var receipt = new PlayerRewardReceipt(
            Guid.NewGuid(),
            requestId,
            playerId,
            500,
            1001,
            2,
            "administrator-reward",
            DateTimeOffset.UtcNow);
        var expectedResult = Applied(receipt);
        var grainClient = new StubPlayerGrainClient(
            (_, _) => Task.FromResult(expectedResult));
        var service = new RewardService(grainClient);
        var request = new GrantRewardRequest(requestId, 500, 1001, 2, "administrator-reward");

        var result = await service.GrantAsync(playerId, request, CancellationToken.None);

        Assert.Same(expectedResult, result);
        Assert.Equal(playerId, grainClient.LastPlayerId);
        Assert.Equal(requestId, grainClient.LastCommand?.RequestId);
        Assert.Equal(500, grainClient.LastCommand?.GoldAmount);
        Assert.Equal(1001, grainClient.LastCommand?.ItemId);
        Assert.Equal(2, grainClient.LastCommand?.ItemQuantity);
        Assert.Equal("administrator-reward", grainClient.LastCommand?.Reason);
    }

    [Fact]
    public async Task GrantAsyncConvertsMissingNullableValuesToExplicitInvalidCommandValues()
    {
        var rejectedResult = Rejected(PlayerRewardCommandError.InvalidRequest);
        var grainClient = new StubPlayerGrainClient(
            (_, _) => Task.FromResult(rejectedResult));
        var service = new RewardService(grainClient);
        var request = new GrantRewardRequest(null, 0, null, null, null);

        var result = await service.GrantAsync(Guid.NewGuid(), request, CancellationToken.None);

        Assert.Same(rejectedResult, result);
        Assert.Equal(Guid.Empty, grainClient.LastCommand?.RequestId);
        Assert.Equal(string.Empty, grainClient.LastCommand?.Reason);
    }

    [Fact]
    public async Task GrantAsyncPreservesPlayerGrainBusinessError()
    {
        var expectedResult = Rejected(PlayerRewardCommandError.IdempotencyConflict);
        var grainClient = new StubPlayerGrainClient(
            (_, _) => Task.FromResult(expectedResult));
        var service = new RewardService(grainClient);
        var request = new GrantRewardRequest(Guid.NewGuid(), 100, null, null, "conflicting-request");

        var result = await service.GrantAsync(Guid.NewGuid(), request, CancellationToken.None);

        Assert.Same(expectedResult, result);
    }

    [Fact]
    public async Task GrantAsyncCancelsOnlyHttpWaitAfterGrainCallHasStarted()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var receipt = new PlayerRewardReceipt(
            Guid.NewGuid(),
            requestId,
            playerId,
            100,
            null,
            null,
            "complete-after-start",
            DateTimeOffset.UtcNow);
        var pendingGrainCall = new TaskCompletionSource<PlayerRewardCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var grainClient = new StubPlayerGrainClient((_, _) => pendingGrainCall.Task);
        var service = new RewardService(grainClient);
        var request = new GrantRewardRequest(requestId, 100, null, null, "complete-after-start");
        using var cancellationSource = new CancellationTokenSource();

        var operation = service.GrantAsync(playerId, request, cancellationSource.Token);

        Assert.NotNull(grainClient.LastCommand);
        cancellationSource.Cancel();

        // API 대기는 취소되지만 CancellationToken이 없는 실제 Grain Task는 그대로 남아 있습니다.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(pendingGrainCall.Task.IsCompleted);

        pendingGrainCall.SetResult(Applied(receipt));
        Assert.Equal(PlayerRewardCommandStatus.Applied, (await pendingGrainCall.Task).Status);
    }

    [Fact]
    public async Task GrantAsyncDoesNotStartGrainCallWhenRequestWasAlreadyCancelled()
    {
        var grainClient = new StubPlayerGrainClient(
            (_, _) => throw new InvalidOperationException("취소된 요청에서 Grain이 호출되면 안 됩니다."));
        var service = new RewardService(grainClient);
        var request = new GrantRewardRequest(Guid.NewGuid(), 100, null, null, "cancelled-before-start");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GrantAsync(Guid.NewGuid(), request, cancellationSource.Token));

        Assert.Equal(0, grainClient.CallCount);
    }

    private static PlayerRewardCommandResult Applied(PlayerRewardReceipt receipt)
    {
        return new PlayerRewardCommandResult(
            IsReplay: false,
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

    /// <summary>Grain 호출을 기록하고 테스트가 지정한 비동기 결과를 반환하는 대역입니다.</summary>
    private sealed class StubPlayerGrainClient(
        Func<Guid, GrantPlayerRewardCommand, Task<PlayerRewardCommandResult>> grantHandler)
        : IPlayerGrainClient
    {
        public int CallCount { get; private set; }

        public Guid? LastPlayerId { get; private set; }

        public GrantPlayerRewardCommand? LastCommand { get; private set; }

        /// <inheritdoc />
        public Task<PlayerRewardCommandResult> GrantAdminRewardAsync(
            Guid playerId,
            GrantPlayerRewardCommand command)
        {
            CallCount++;
            LastPlayerId = playerId;
            LastCommand = command;
            return grantHandler(playerId, command);
        }
    }
}
