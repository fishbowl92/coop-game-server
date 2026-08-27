using CoopGameServer.GrainContracts.GameRooms;

namespace CoopGameServer.Api.Application.GameRooms;

/// <summary>HTTP 게임 방 요청을 roomId에 해당하는 GameRoomGrain으로 전달합니다.</summary>
public sealed class GameRoomService(IGrainFactory grainFactory)
{
    /// <summary>게임 방의 현재 상태를 조회합니다.</summary>
    public Task<GameRoomSnapshot?> GetAsync(Guid roomId, CancellationToken cancellationToken)
    {
        return roomId == Guid.Empty
            ? Task.FromResult<GameRoomSnapshot?>(null)
            : GetRoom(roomId).GetAsync().WaitAsync(cancellationToken);
    }

    /// <summary>Ready 상태 게임 방을 시작하고 사전 구성 파티를 InGame으로 전환합니다.</summary>
    public Task<GameRoomCommandResult> StartAsync(
        Guid roomId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return roomId == Guid.Empty
            ? Task.FromResult(Failure(GameRoomCommandError.InvalidRoomId))
            : GetRoom(roomId).StartAsync(requestId).WaitAsync(cancellationToken);
    }

    /// <summary>게임 방을 완료하고 사전 구성 파티를 Active 로비 상태로 되돌립니다.</summary>
    public Task<GameRoomCommandResult> CompleteAsync(
        Guid roomId,
        Guid requestId,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (roomId == Guid.Empty)
        {
            return Task.FromResult(Failure(GameRoomCommandError.InvalidRoomId));
        }

        // HTTP에서는 사람이 읽기 쉬운 문자열을 받고, Orleans 계약 경계에서는 고정된 enum으로 바꿉니다.
        // 해석할 수 없는 값은 None으로 바꿔 Grain의 단일 상태 규칙에서 InvalidOutcome으로 판정합니다.
        // 이렇게 해야 잘못된 최초 요청도 Grain의 멱등성 기록에 남아 같은 requestId 재사용을 막을 수 있습니다.
        var parsedOutcome = Enum.TryParse<GameOutcome>(outcome, ignoreCase: true, out var candidateOutcome)
            && Enum.IsDefined(candidateOutcome)
            ? candidateOutcome
            : GameOutcome.None;

        return GetRoom(roomId)
            .CompleteAsync(requestId, parsedOutcome)
            .WaitAsync(cancellationToken);
    }

    private IGameRoomGrain GetRoom(Guid roomId)
    {
        return grainFactory.GetGrain<IGameRoomGrain>(roomId);
    }

    private static GameRoomCommandResult Failure(GameRoomCommandError error)
    {
        return new GameRoomCommandResult(
            IsReplay: false,
            Error: error,
            Room: null,
            FailedPartyId: null,
            PartyError: null);
    }
}
