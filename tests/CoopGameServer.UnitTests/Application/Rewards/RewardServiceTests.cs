using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.Persistence.Rewards;

namespace CoopGameServer.UnitTests.Application.Rewards;

/// <summary>
/// 기존 HTTP 보상 계약과 새 Persistence Writer 계약을 연결하는 임시 어댑터를 검증합니다.
/// </summary>
/// <remarks>
/// 실제 트랜잭션과 동시성은 PostgreSQL 통합 테스트가 담당합니다. 여기서는 계층 사이의
/// 입력 변환과 예상 업무 오류 매핑만 빠르게 검증합니다.
/// </remarks>
public sealed class RewardServiceTests
{
    [Fact]
    public async Task GrantAsyncMapsHttpRequestToWriterCommandAndReturnsReceipt()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var receipt = new RewardWriteReceipt(
            Guid.NewGuid(),
            requestId,
            playerId,
            500,
            1001,
            2,
            "administrator-reward",
            DateTimeOffset.UtcNow);
        var writer = new StubRewardWriter(
            _ => Task.FromResult(RewardWriteResult.Applied(receipt)));
        var service = new RewardService(writer);
        var request = new GrantRewardRequest(requestId, 500, 1001, 2, "administrator-reward");

        var result = await service.GrantAsync(playerId, request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(receipt, result.Receipt);
        Assert.False(result.IsReplay);
        Assert.Equal(requestId, writer.LastCommand?.RequestId);
        Assert.Equal(playerId, writer.LastCommand?.PlayerId);
        Assert.Equal(500, writer.LastCommand?.GoldAmount);
        Assert.Equal(1001, writer.LastCommand?.ItemId);
        Assert.Equal(2, writer.LastCommand?.ItemQuantity);
        Assert.Equal("administrator-reward", writer.LastCommand?.Reason);
    }

    [Fact]
    public async Task GrantAsyncMapsPlayerNotFoundToNull()
    {
        var writer = new StubRewardWriter(
            _ => Task.FromResult(RewardWriteResult.Failed(RewardWriteError.PlayerNotFound)));
        var service = new RewardService(writer);
        var request = new GrantRewardRequest(Guid.NewGuid(), 100, null, null, "missing-player");

        var result = await service.GrantAsync(Guid.NewGuid(), request, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GrantAsyncMapsIdempotencyConflictToExistingApiException()
    {
        var writer = new StubRewardWriter(
            _ => Task.FromResult(RewardWriteResult.Failed(RewardWriteError.IdempotencyConflict)));
        var service = new RewardService(writer);
        var request = new GrantRewardRequest(Guid.NewGuid(), 100, null, null, "conflicting-request");

        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(
            () => service.GrantAsync(Guid.NewGuid(), request, CancellationToken.None));
    }

    [Fact]
    public async Task GrantAsyncDoesNotDetachWriterAfterCancellationOnceWriteHasStarted()
    {
        var playerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var receipt = new RewardWriteReceipt(
            Guid.NewGuid(),
            requestId,
            playerId,
            100,
            null,
            null,
            "complete-after-start",
            DateTimeOffset.UtcNow);
        var pendingWrite = new TaskCompletionSource<RewardWriteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new StubRewardWriter(_ => pendingWrite.Task);
        var service = new RewardService(writer);
        var request = new GrantRewardRequest(requestId, 100, null, null, "complete-after-start");
        using var cancellationSource = new CancellationTokenSource();

        var operation = service.GrantAsync(playerId, request, cancellationSource.Token);

        // Writer 호출 뒤 HTTP 토큰이 취소돼도 작업을 분리하지 않고 Writer의 결론을 기다립니다.
        Assert.NotNull(writer.LastCommand);
        cancellationSource.Cancel();
        Assert.False(operation.IsCompleted);

        pendingWrite.SetResult(RewardWriteResult.Applied(receipt));

        var result = await operation;
        Assert.NotNull(result);
        Assert.Same(receipt, result.Receipt);
    }

    [Fact]
    public async Task GrantAsyncDoesNotStartWriterWhenRequestWasAlreadyCancelled()
    {
        var writer = new StubRewardWriter(
            _ => throw new InvalidOperationException("취소된 요청에서 Writer가 호출되면 안 됩니다."));
        var service = new RewardService(writer);
        var request = new GrantRewardRequest(Guid.NewGuid(), 100, null, null, "cancelled-before-start");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GrantAsync(Guid.NewGuid(), request, cancellationSource.Token));

        Assert.Null(writer.LastCommand);
    }

    /// <summary>테스트가 지정한 결과를 반환하고 전달받은 명령을 기록하는 Writer 대역입니다.</summary>
    private sealed class StubRewardWriter(
        Func<RewardWriteCommand, Task<RewardWriteResult>> writeHandler) : IRewardWriter
    {
        /// <summary>어댑터가 마지막으로 변환해 전달한 명령입니다.</summary>
        public RewardWriteCommand? LastCommand { get; private set; }

        /// <inheritdoc />
        public Task<RewardWriteResult> WriteAsync(RewardWriteCommand command)
        {
            LastCommand = command;
            return writeHandler(command);
        }
    }
}
