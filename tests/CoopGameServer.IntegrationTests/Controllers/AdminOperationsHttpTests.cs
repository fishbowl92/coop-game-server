using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Administration;
using CoopGameServer.Contracts.Rewards;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace CoopGameServer.IntegrationTests.Controllers;

/// <summary>관리 API의 JWT 인증·역할 인가·감사 저장을 실제 HTTP와 Orleans로 검증합니다.</summary>
[Collection(OrleansTestClusterSuite.Name)]
public sealed class AdminOperationsHttpTests(OrleansTestClusterFixture fixture)
{
    private const string TestIssuer = "admin-http-tests";
    private const string TestKey = "isolated-admin-http-signing-key-2026";

    [Fact]
    public async Task AdministratorCanLookupGrantReplayAndReadAuditedHistory()
    {
        var targetPlayerId = Guid.NewGuid();
        var administratorPlayerId = Guid.NewGuid();
        var administratorAccountId = Guid.NewGuid();
        var targetNickname = $"T{targetPlayerId:N}"[..20];
        await fixture.RegisterPlayersAsync(targetPlayerId, administratorPlayerId);
        await SeedAdministratorAsync(administratorAccountId, administratorPlayerId, "http_admin");

        await using var database = fixture.CreateDbContext();
        var target = await database.Players.SingleAsync(player => player.Id == targetPlayerId);
        target.Rename(targetNickname, DateTimeOffset.UtcNow);
        await database.SaveChangesAsync();

        await using var factory = new ApiFactory(fixture, database.Database.GetConnectionString()!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        var lookupPath = $"/api/admin/players/lookup?query={Uri.EscapeDataString(targetNickname)}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(lookupPath)).StatusCode);
        Authenticate(client, administratorPlayerId, Guid.NewGuid(), AccountRole.Player);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(lookupPath)).StatusCode);
        Authenticate(client, administratorPlayerId, null, AccountRole.Administrator);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(lookupPath)).StatusCode);
        Authenticate(client, administratorPlayerId, Guid.Empty, AccountRole.Administrator, "not-a-guid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(lookupPath)).StatusCode);

        Authenticate(client, administratorPlayerId, administratorAccountId, AccountRole.Administrator);
        var lookup = await client.GetFromJsonAsync<AdminPlayerLookupResponse>(lookupPath);
        Assert.NotNull(lookup);
        Assert.Equal(targetPlayerId, lookup.PlayerId);
        Assert.Equal(targetNickname, lookup.Nickname);

        var request = new GrantRewardRequest(
            Guid.NewGuid(),
            450,
            6101,
            2,
            "http-admin-recovery");
        var rewardPath = $"/api/players/{targetPlayerId}/rewards";
        var firstResponse = await client.PostAsJsonAsync(rewardPath, request);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstReceipt = await firstResponse.Content.ReadFromJsonAsync<GrantRewardResponse>();
        Assert.NotNull(firstReceipt);
        Assert.False(firstReceipt.IsReplay);

        var replayResponse = await client.PostAsJsonAsync(rewardPath, request);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayReceipt = await replayResponse.Content.ReadFromJsonAsync<GrantRewardResponse>();
        Assert.NotNull(replayReceipt);
        Assert.True(replayReceipt.IsReplay);
        Assert.Equal(firstReceipt.RewardAuditId, replayReceipt.RewardAuditId);

        var history = await client.GetFromJsonAsync<AdminRewardHistoryResponse[]>(
            $"/api/admin/players/{targetPlayerId}/reward-history?limit=20");
        var historyEntry = Assert.Single(history!);
        Assert.Equal(request.RequestId, historyEntry.RequestId);
        Assert.Equal(administratorAccountId, historyEntry.AdministratorAccountId);
        Assert.Equal("http_admin", historyEntry.AdministratorLoginId);

        await using var assertionDatabase = fixture.CreateDbContext();
        Assert.Equal(1, await assertionDatabase.AdminAudits.CountAsync(audit =>
            audit.RequestId == request.RequestId &&
            audit.AdministratorAccountId == administratorAccountId));
    }

    [Fact]
    public async Task SameRequestIdFromDifferentAdministratorIsConflict()
    {
        var targetPlayerId = Guid.NewGuid();
        var firstAdminPlayerId = Guid.NewGuid();
        var secondAdminPlayerId = Guid.NewGuid();
        var firstAdminAccountId = Guid.NewGuid();
        var secondAdminAccountId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(targetPlayerId, firstAdminPlayerId, secondAdminPlayerId);
        await SeedAdministratorAsync(firstAdminAccountId, firstAdminPlayerId, "first_http_admin");
        await SeedAdministratorAsync(secondAdminAccountId, secondAdminPlayerId, "second_http_admin");

        await using var database = fixture.CreateDbContext();
        await using var factory = new ApiFactory(fixture, database.Database.GetConnectionString()!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        var request = new GrantRewardRequest(Guid.NewGuid(), 10, null, null, "actor-conflict");
        var path = $"/api/players/{targetPlayerId}/rewards";

        Authenticate(client, firstAdminPlayerId, firstAdminAccountId, AccountRole.Administrator);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(path, request)).StatusCode);
        Authenticate(client, secondAdminPlayerId, secondAdminAccountId, AccountRole.Administrator);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, request)).StatusCode);
    }

    /// <summary>HTTP에서 시작한 Trace가 Orleans를 지나 PostgreSQL 보상 구간까지 같은 ID로 이어지는지 검증합니다.</summary>
    [Fact]
    public async Task RewardRequestPropagatesTraceWithoutRecordingRequestBodyOrCredentials()
    {
        const string privateReason = "trace-private-reason-marker";
        var activities = new ConcurrentQueue<CapturedActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == CoopGameServerTelemetry.ActivitySourceName ||
                source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(new CapturedActivity(
                activity.Source.Name,
                activity.OperationName,
                activity.Kind,
                activity.TraceId,
                activity.ParentSpanId,
                activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString()))),
        };
        ActivitySource.AddActivityListener(listener);

        var targetPlayerId = Guid.NewGuid();
        var administratorPlayerId = Guid.NewGuid();
        var administratorAccountId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await fixture.RegisterPlayersAsync(targetPlayerId, administratorPlayerId);
        await SeedAdministratorAsync(
            administratorAccountId,
            administratorPlayerId,
            $"trace_admin_{administratorAccountId:N}"[..20]);

        await using var database = fixture.CreateDbContext();
        await using var factory = new ApiFactory(fixture, database.Database.GetConnectionString()!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        Authenticate(client, administratorPlayerId, administratorAccountId, AccountRole.Administrator);

        var response = await client.PostAsJsonAsync(
            $"/api/players/{targetPlayerId}/rewards",
            new GrantRewardRequest(requestId, 25, null, null, privateReason));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rewardActivity = Assert.Single(activities, activity =>
            activity.SourceName == CoopGameServerTelemetry.ActivitySourceName &&
            activity.OperationName == "postgresql.reward.write");
        Assert.Contains(activities, activity =>
            activity.Kind == ActivityKind.Server &&
            activity.TraceId == rewardActivity.TraceId);
        Assert.NotEqual(default, rewardActivity.ParentSpanId);
        Assert.Equal(requestId.ToString(), rewardActivity.Tags["coopgame.request.id"]);
        Assert.Equal(targetPlayerId.ToString(), rewardActivity.Tags["coopgame.player.id"]);

        var exportedText = string.Join(
            '|',
            rewardActivity.Tags.Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(privateReason, exportedText, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKey, exportedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", exportedText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SeedAdministratorAsync(Guid accountId, Guid playerId, string loginId)
    {
        var account = new Account(accountId, playerId, loginId, AccountRole.Administrator, DateTimeOffset.UtcNow);
        account.SetPasswordHash("integration-test-password-hash");
        await using var database = fixture.CreateDbContext();
        database.Accounts.Add(account);
        await database.SaveChangesAsync();
    }

    private static void Authenticate(
        HttpClient client,
        Guid playerId,
        Guid? accountId,
        AccountRole role,
        string? rawAccountId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, playerId.ToString()),
            new(ClaimTypes.Role, role.ToString()),
        };
        if (rawAccountId is not null)
        {
            claims.Add(new Claim(CurrentPlayerClaims.AccountIdClaimType, rawAccountId));
        }
        else if (accountId.HasValue)
        {
            claims.Add(new Claim(CurrentPlayerClaims.AccountIdClaimType, accountId.Value.ToString()));
        }

        var token = new JwtSecurityToken(
            TestIssuer,
            TestIssuer,
            claims,
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
            builder.UseSetting("Authentication:Jwt:Issuer", TestIssuer);
            builder.UseSetting("Authentication:Jwt:Audience", TestIssuer);
            builder.UseSetting("Authentication:Jwt:SigningKey", TestKey);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IGrainFactory>();
                services.AddSingleton(fixture.Cluster.GrainFactory);
            });
        }
    }

    private sealed record CapturedActivity(
        string SourceName,
        string OperationName,
        ActivityKind Kind,
        ActivityTraceId TraceId,
        ActivitySpanId ParentSpanId,
        IReadOnlyDictionary<string, string?> Tags);
}
