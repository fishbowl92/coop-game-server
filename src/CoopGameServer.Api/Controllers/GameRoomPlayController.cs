using CoopGameServer.Api.Authentication;
using CoopGameServer.GrainContracts.GameRooms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoopGameServer.Api.Controllers;

/// <summary>플레이어 신원은 요청 본문이 아니라 검증된 JWT에서만 가져옵니다.</summary>
[ApiController, Authorize, Route("api/game-rooms/{roomId:guid}")]
public sealed class GameRoomPlayController(IGrainFactory grains) : ControllerBase
{
    [HttpGet("play-state")]
    public async Task<IActionResult> GetState(Guid roomId)
    {
        if (!User.TryGetPlayerId(out var playerId)) return Unauthorized();
        var result = await grains.GetGrain<IGameRoomGrain>(roomId).GetPlayerViewAsync(playerId);
        return result.Error == "None" ? Ok(result) : Error(result.Error);
    }
    [HttpPost("connect")]
    public Task<IActionResult> Connect(Guid roomId, ConnectRoomRequest request) => Connection(roomId,
        new(request.RequestId, default, GameRoomConnectionAction.Connect));

    [HttpPost("reconnect"), EnableRateLimiting("room-reconnect")]
    public Task<IActionResult> Reconnect(Guid roomId, ReconnectRoomRequest request) => Connection(roomId,
        new(request.RequestId, default, GameRoomConnectionAction.Reconnect, Generation: request.ExpectedGeneration));

    [HttpPost("heartbeat"), EnableRateLimiting("room-heartbeat")]
    public Task<IActionResult> Heartbeat(Guid roomId, RoomCredentialRequest request) => Connection(roomId,
        new(Guid.Empty, default, GameRoomConnectionAction.Heartbeat, request.ConnectionId, request.Generation));

    [HttpPost("disconnect")]
    public Task<IActionResult> Disconnect(Guid roomId, RoomSessionCommandRequest request) => Connection(roomId,
        new(request.RequestId, default, GameRoomConnectionAction.Disconnect, request.ConnectionId, request.Generation));

    [HttpPost("start-combat")]
    public Task<IActionResult> StartCombat(Guid roomId, RoomSessionCommandRequest request) => Connection(roomId,
        new(request.RequestId, default, GameRoomConnectionAction.StartCombat, request.ConnectionId, request.Generation));

    [HttpPost("basic-attack")]
    public Task<IActionResult> Attack(Guid roomId, RoomAttackRequest request) => Combat(roomId, request, CombatActionKind.BasicAttack);

    [HttpPost("use-skill")]
    public Task<IActionResult> Skill(Guid roomId, RoomAttackRequest request) => Combat(roomId, request, CombatActionKind.UseSkill);

    private async Task<IActionResult> Connection(Guid roomId, GameRoomConnectionCommand command)
    {
        if (!User.TryGetPlayerId(out var playerId)) return Unauthorized();
        var result = await grains.GetGrain<IGameRoomGrain>(roomId).ExecuteConnectionAsync(command with { PlayerId = playerId });
        return result.Error == "None" ? Ok(result) : Error(result.Error);
    }

    private async Task<IActionResult> Combat(Guid roomId, RoomAttackRequest request, CombatActionKind action)
    {
        if (!User.TryGetPlayerId(out var playerId)) return Unauthorized();
        var result = await grains.GetGrain<IGameRoomGrain>(roomId).ExecuteCombatAsync(new(request.RequestId,
            playerId, request.Sequence, action, request.KnownStateVersion, request.ConnectionId, request.Generation));
        return result.Error == GameRoomCombatError.None ? Ok(result) : Error(result.Error.ToString());
    }

    private ObjectResult Error(string error)
    {
        var status = error switch
        {
            "RoomNotCreated" => StatusCodes.Status404NotFound,
            "PlayerNotInRoom" => StatusCodes.Status403Forbidden,
            "InvalidCommand" or "InvalidConnectionGeneration" or "InvalidConnectionId" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict,
        };
        // 접속 자격이나 타인의 스냅샷을 오류 응답에 포함하지 않습니다.
        return StatusCode(status, new ProblemDetails { Status = status, Title = error });
    }
}

public sealed record ConnectRoomRequest(Guid RequestId);
public sealed record ReconnectRoomRequest(Guid RequestId, long ExpectedGeneration);
public sealed record RoomCredentialRequest(Guid ConnectionId, long Generation);
public sealed record RoomSessionCommandRequest(Guid RequestId, Guid ConnectionId, long Generation);
public sealed record RoomAttackRequest(Guid RequestId, Guid ConnectionId, long Generation, long Sequence, long? KnownStateVersion = null);
