using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using CoopGameServer.Contracts.Players;
using CoopGameServer.GrainContracts.Players;
using CoopGameServer.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace CoopGameServer.IntegrationTests.Controllers;

/// <summary>진행도 API의 JWT 인증·본인 인가·Orleans·PostgreSQL·Redis 연결을 실제 HTTP로 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class PlayerProgressionHttpTests(OrleansTestClusterFixture fixture)
{
    private const string TestKey = "isolated-progression-http-signing-key-2026";

    [Fact]
    public async Task ProgressionEndpointEnforcesOwnershipAndReturnsCombinedProgression()
    {
        var playerId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(playerId);
        var player = fixture.Cluster.Client.GetGrain<IPlayerGrain>(playerId);
        await player.GrantAdminRewardAsync(
            new GrantPlayerRewardCommand(
                Guid.NewGuid(),
                725,
                5201,
                3,
                "progression-http-test"));

        await using var database = fixture.CreateDbContext();
        await using var factory = new ApiFactory(fixture, database.Database.GetConnectionString()!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        var path = $"/api/players/{playerId}/progression?pageSize=20";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);

        Authenticate(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);

        Authenticate(client, playerId);
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var progression = await response.Content.ReadFromJsonAsync<PlayerProgressionResponse>();

        Assert.NotNull(progression);
        Assert.Equal(playerId, progression.PlayerId);
        Assert.False(string.IsNullOrWhiteSpace(progression.Nickname));
        Assert.Equal(725, progression.Gold);
        Assert.Contains(progression.Items, item => item.ItemId == 5201 && item.Quantity == 3);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/players/{playerId}/progression?pageSize=0")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/players/{playerId}/progression?continuationToken=invalid")).StatusCode);
    }

    [Fact]
    public async Task NicknameChangeInvalidatesCachedProgression()
    {
        var playerId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(playerId);

        await using var database = fixture.CreateDbContext();
        await using var factory = new ApiFactory(fixture, database.Database.GetConnectionString()!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        Authenticate(client, playerId);

        var progressionPath = $"/api/players/{playerId}/progression?pageSize=20";
        var firstProgression = await client.GetFromJsonAsync<PlayerProgressionResponse>(progressionPath);
        Assert.NotNull(firstProgression);

        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var cacheKey = $"coopgame:test:player-progression:v1:{playerId:N}";
        Assert.True(await redis.GetDatabase().KeyExistsAsync(cacheKey));

        var nickname = $"R{playerId:N}"[..20];
        var renameResponse = await client.PatchAsJsonAsync(
            $"/api/players/{playerId}/nickname",
            new UpdatePlayerNicknameRequest(nickname));
        renameResponse.EnsureSuccessStatusCode();

        Assert.False(await redis.GetDatabase().KeyExistsAsync(cacheKey));

        var refreshedProgression = await client.GetFromJsonAsync<PlayerProgressionResponse>(progressionPath);
        Assert.NotNull(refreshedProgression);
        Assert.Equal(nickname, refreshedProgression.Nickname);
    }

    private static void Authenticate(HttpClient client, Guid playerId)
    {
        var token = new JwtSecurityToken(
            "progression-http-tests",
            "progression-http-tests",
            [new Claim(JwtRegisteredClaimNames.Sub, playerId.ToString())],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey)),
                SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
    }

    private sealed class ApiFactory(
        OrleansTestClusterFixture fixture,
        string connectionString) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:GameDb", connectionString);
            builder.UseSetting("Authentication:Jwt:Issuer", "progression-http-tests");
            builder.UseSetting("Authentication:Jwt:Audience", "progression-http-tests");
            builder.UseSetting("Authentication:Jwt:SigningKey", TestKey);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IGrainFactory>();
                services.AddSingleton(fixture.Cluster.GrainFactory);
            });
        }
    }
}
