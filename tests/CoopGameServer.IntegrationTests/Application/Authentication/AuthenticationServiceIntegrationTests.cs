using CoopGameServer.Api.Application.Authentication;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Authentication;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.Application.Authentication;

/// <summary>실제 PostgreSQL의 UNIQUE·외래 키 제약과 함께 회원 가입을 검증합니다.</summary>
[Collection(PostgreSqlIntegrationTestGroup.Name)]
public sealed class AuthenticationServiceIntegrationTests(PostgreSqlDatabaseFixture databaseFixture) : IAsyncLifetime
{
    /// <summary>각 테스트 전 테스트 컨테이너의 데이터만 비웁니다.</summary>
    public Task InitializeAsync()
    {
        return databaseFixture.ResetDataAsync();
    }

    /// <summary>Fixture가 컨테이너를 정리하므로 테스트별 추가 정리는 없습니다.</summary>
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RegisterWithSameLoginIdRejectsSecondRequestAndDoesNotLeaveSecondPlayer()
    {
        await using (var firstDbContext = databaseFixture.CreateDbContext())
        {
            await CreateService(firstDbContext).RegisterAsync(
                new RegisterAccountRequest("same_login", "first-password", "FirstAccountPlayer"),
                CancellationToken.None);
        }

        await using (var secondDbContext = databaseFixture.CreateDbContext())
        {
            await Assert.ThrowsAsync<DuplicateAccountException>(
                () => CreateService(secondDbContext).RegisterAsync(
                    new RegisterAccountRequest("SAME_LOGIN", "second-password", "SecondAccountPlayer"),
                    CancellationToken.None));
        }

        await using var assertionDbContext = databaseFixture.CreateDbContext();
        Assert.Single(await assertionDbContext.Accounts.ToListAsync());
        Assert.Single(await assertionDbContext.Players.ToListAsync());
    }

    /// <summary>실제 PostgreSQL DbContext와 인증 서비스를 연결합니다.</summary>
    private static AuthenticationService CreateService(GameDbContext gameDbContext)
    {
        var tokenService = new JwtTokenService(new JwtOptions
        {
            Issuer = "CoopGameServer.IntegrationTests",
            Audience = "CoopGameServer.IntegrationTests.Client",
            SigningKey = "integration-test-signing-key-that-is-long-enough-123",
        });

        return new AuthenticationService(
            gameDbContext,
            new PasswordHasher<Account>(),
            tokenService);
    }
}
