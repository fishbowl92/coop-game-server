using CoopGameServer.Api.Controllers;
using CoopGameServer.Api.Data;
using CoopGameServer.Contracts.Players;
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
}
