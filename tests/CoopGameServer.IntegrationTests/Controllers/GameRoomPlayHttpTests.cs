using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using CoopGameServer.Api.Controllers;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace CoopGameServer.IntegrationTests.Controllers;

/// <summary>실제 API 시작 구성·JWT 검증·HTTP 라우팅에서 Orleans와 PostgreSQL까지 연결합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class GameRoomPlayHttpTests(OrleansTestClusterFixture fixture)
{
    // 테스트 프로세스에서만 사용하는 키입니다. 개발 User Secrets를 읽거나 변경하지 않습니다.
    private const string TestKey = "isolated-http-test-signing-key-not-for-production-2026";

    [Fact]
    public async Task AuthenticatedCombatRejectsStrangersStaleCredentialsAndExcessHeartbeat()
    {
        var clock = CombatTestTimeProvider.Shared;
        clock.Set(DateTimeOffset.UtcNow);
        try
        {
            var players = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            await fixture.RegisterPlayersAsync(players);
            var roomId = Guid.NewGuid();
            var room = fixture.Cluster.GrainFactory.GetGrain<IGameRoomGrain>(roomId);
            await room.CreateAsync(Guid.NewGuid(), new MatchAssignment(roomId, "coop-dungeon-normal-v1", [], players, clock.GetUtcNow()));
            await using var database = fixture.CreateDbContext();
            await using var factory = new ApiFactory(fixture, database.Database.GetConnectionString()!);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var path = $"/api/game-rooms/{roomId}";

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path + "/connect", new ConnectRoomRequest(Guid.NewGuid()))).StatusCode);
            Authenticate(client, Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/connect", new ConnectRoomRequest(Guid.NewGuid()))).StatusCode);
            var connections = new Dictionary<Guid, GameRoomConnectionResult>();
            foreach (var player in players)
            {
                Authenticate(client, player);
                var response = await client.PostAsJsonAsync(path + "/connect", new ConnectRoomRequest(Guid.NewGuid()));
                response.EnsureSuccessStatusCode();
                connections[player] = (await response.Content.ReadFromJsonAsync<GameRoomConnectionResult>())!;
            }
            Authenticate(client, players[0]);
            var own = connections[players[0]];
            var started = await client.PostAsJsonAsync(path + "/start-combat", new RoomSessionCommandRequest(Guid.NewGuid(), own.ConnectionId!.Value, 1));
            started.EnsureSuccessStatusCode();
            var attack = new RoomAttackRequest(Guid.NewGuid(), own.ConnectionId.Value, 1, 1);
            (await client.PostAsJsonAsync(path + "/basic-attack", attack)).EnsureSuccessStatusCode();
            var replay = await (await client.PostAsJsonAsync(path + "/basic-attack", attack)).Content.ReadFromJsonAsync<GameRoomCombatResult>();
            Assert.True(replay!.IsReplay);

            var replacement = await client.PostAsJsonAsync(path + "/reconnect", new ReconnectRoomRequest(Guid.NewGuid(), 1));
            replacement.EnsureSuccessStatusCode();
            var renewed = (await replacement.Content.ReadFromJsonAsync<GameRoomConnectionResult>())!;
            Assert.Equal(2, renewed.Generation);
            var stale = await client.PostAsJsonAsync(path + "/basic-attack", attack with { RequestId = Guid.NewGuid(), Sequence = 2 });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var view = (await client.GetFromJsonAsync<GameRoomConnectionResult>(path + "/play-state"))!;
            Assert.Null(view.ConnectionId);
            Assert.Equal(2, view.Generation);
            Assert.Equal(80, view.Room!.EnemyCurrentHealth);

            // 요청을 충분히 모아 고정 윈도 경계에 걸려도 최소 한 건의 429를 확인합니다.
            var beats = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.PostAsJsonAsync(path + "/heartbeat",
                new RoomCredentialRequest(renewed.ConnectionId!.Value, 2))));
            Assert.Contains(beats, response => response.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.All(beats.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests), response => Assert.NotNull(response.Headers.RetryAfter));
            foreach (var beat in beats) beat.Dispose();
        }
        finally { clock.Reset(); }
    }

    private static void Authenticate(HttpClient client, Guid player)
    {
        var token = new JwtSecurityToken("http-tests", "http-tests", [new Claim(JwtRegisteredClaimNames.Sub, player.ToString())],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    private sealed class ApiFactory(OrleansTestClusterFixture fixture, string connectionString) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:GameDb", connectionString);
            builder.UseSetting("Authentication:Jwt:Issuer", "http-tests");
            builder.UseSetting("Authentication:Jwt:Audience", "http-tests");
            builder.UseSetting("Authentication:Jwt:SigningKey", TestKey);
            builder.ConfigureTestServices(services =>
            {
                // localhost 운영 Silo 연결 대신 테스트 클러스터를 사용합니다. 인증·인가·MVC는 실제 구성입니다.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IGrainFactory>();
                services.AddSingleton(fixture.Cluster.GrainFactory);
            });
        }
    }
}
