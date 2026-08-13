using CoopGameServer.Api.Controllers;
using CoopGameServer.Contracts.Players;
using CoopGameServer.Domain.Players;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.UnitTests.Controllers;

/// <summary>
/// 플레이어 생성 API가 요청을 받아 DB 작업까지 연결하는지 검증합니다.
/// </summary>
public sealed class PlayersControllerTests
{
    [Fact]
    public async Task CreatePlayerSavesPlayerAndReturnsCreatedResponse()
    {
        // 테스트마다 고유한 메모리 DB 이름을 사용해 다른 테스트의 데이터와 섞이지 않게 합니다.
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        // InMemory 제공자는 PostgreSQL의 UNIQUE 인덱스와 같은 관계형 DB 제약 조건을 완전히 재현하지 않습니다.
        // 따라서 중복 닉네임의 409 검증은 실제 PostgreSQL을 사용하는 통합 테스트에서 별도로 다룹니다.

        await using var gameDbContext = new GameDbContext(options);
        var controller = new PlayersController(gameDbContext);

        var actionResult = await controller.CreatePlayer(
            new CreatePlayerRequest("  Minwoo  "),
            CancellationToken.None);

        // HTTP 201 Created와 함께, 이후 조회할 수 있는 위치 정보가 반환되어야 합니다.
        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        var response = Assert.IsType<PlayerResponse>(createdResult.Value);

        Assert.Equal(nameof(PlayersController.GetPlayerById), createdResult.ActionName);
        Assert.Equal("Minwoo", response.Nickname);
        Assert.NotEqual(Guid.Empty, response.Id);

        // 응답만 만든 것이 아니라 EF Core를 통해 실제 저장 작업까지 요청됐는지 확인합니다.
        var savedPlayer = await gameDbContext.Players.SingleAsync();
        Assert.Equal(response.Id, savedPlayer.Id);
        Assert.Equal("Minwoo", savedPlayer.Nickname);
    }

    [Fact]
    public async Task UpdatePlayerNicknameChangesNicknameAndUpdatedAt()
    {
        var options = CreateInMemoryOptions();
        // 컨트롤러는 현재 UTC 시각으로 UpdatedAt을 만들므로, 생성 시각은 항상 그보다 과거여야 합니다.
        // 고정된 미래 날짜를 사용하면 실제 실행 날짜에 따라 시간 순서 검증이 잘못 실패할 수 있습니다.
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var player = new Player(Guid.NewGuid(), "BeforeRename", createdAt);

        await using var gameDbContext = new GameDbContext(options);
        gameDbContext.Players.Add(player);
        await gameDbContext.SaveChangesAsync();

        var controller = new PlayersController(gameDbContext);
        var actionResult = await controller.UpdatePlayerNickname(
            player.Id,
            new UpdatePlayerNicknameRequest("  AfterRename  "),
            CancellationToken.None);

        // 닉네임 변경 성공은 수정된 리소스를 포함한 HTTP 200 OK로 응답합니다.
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PlayerResponse>(okResult.Value);

        Assert.Equal("AfterRename", response.Nickname);
        Assert.Equal(createdAt, response.CreatedAt);
        Assert.True(response.UpdatedAt > createdAt);

        // EF Core가 추적한 변경이 실제 저장소에도 반영됐는지 확인합니다.
        var savedPlayer = await gameDbContext.Players.SingleAsync();
        Assert.Equal("AfterRename", savedPlayer.Nickname);
        Assert.Equal(createdAt, savedPlayer.CreatedAt);
        Assert.Equal(response.UpdatedAt, savedPlayer.UpdatedAt);
    }

    [Fact]
    public async Task UpdatePlayerNicknameReturnsNotFoundWhenPlayerDoesNotExist()
    {
        await using var gameDbContext = new GameDbContext(CreateInMemoryOptions());
        var controller = new PlayersController(gameDbContext);

        var actionResult = await controller.UpdatePlayerNickname(
            Guid.NewGuid(),
            new UpdatePlayerNicknameRequest("NoPlayer"),
            CancellationToken.None);

        // 존재하지 않는 ID는 새 데이터를 만들지 않고 HTTP 404 Not Found를 반환해야 합니다.
        Assert.IsType<NotFoundResult>(actionResult.Result);
        Assert.Empty(gameDbContext.Players);
    }

    [Fact]
    public async Task UpdatePlayerNicknameReturnsBadRequestForWhitespaceNickname()
    {
        var options = CreateInMemoryOptions();
        var player = new Player(Guid.NewGuid(), "ValidNickname", DateTimeOffset.UtcNow);

        await using var gameDbContext = new GameDbContext(options);
        gameDbContext.Players.Add(player);
        await gameDbContext.SaveChangesAsync();

        var controller = new PlayersController(gameDbContext);
        var actionResult = await controller.UpdatePlayerNickname(
            player.Id,
            new UpdatePlayerNicknameRequest("   "),
            CancellationToken.None);

        // 유효하지 않은 요청은 DB 값을 바꾸지 않고 HTTP 400 Bad Request로 끝나야 합니다.
        var badRequestResult = Assert.IsAssignableFrom<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequestResult.StatusCode);

        var savedPlayer = await gameDbContext.Players.SingleAsync();
        Assert.Equal("ValidNickname", savedPlayer.Nickname);
    }

    /// <summary>
    /// 테스트마다 독립된 InMemory 데이터베이스 옵션을 생성합니다.
    /// </summary>
    /// <remarks>
    /// Guid를 데이터베이스 이름으로 사용하면 다른 테스트가 실행한 데이터가 섞이지 않습니다.
    /// InMemory 제공자는 PostgreSQL의 UNIQUE 제약 조건을 재현하지 못하므로, 중복 닉네임 409은 통합 테스트에서 검증합니다.
    /// </remarks>
    private static DbContextOptions<GameDbContext> CreateInMemoryOptions()
    {
        return new DbContextOptionsBuilder<GameDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
    }
}
