using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CoopGameServer.Api.Application.Authentication;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Authentication;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.UnitTests.Application.Authentication;

/// <summary>회원 가입·비밀번호 해시·JWT 발급의 핵심 흐름을 빠르게 검증합니다.</summary>
public sealed class AuthenticationServiceTests
{
    [Fact]
    public async Task RegisterThenLoginStoresOnlyHashAndIssuesTokenForSamePlayer()
    {
        await using var gameDbContext = new GameDbContext(CreateInMemoryOptions());
        var service = CreateService(gameDbContext);
        var request = new RegisterAccountRequest("  Minwoo_92 ", "correct-horse-battery", "TokenPlayer");

        var registered = await service.RegisterAsync(request, CancellationToken.None);
        var loggedIn = await service.LoginAsync(
            new LoginRequest("MINWOO_92", "correct-horse-battery"),
            CancellationToken.None);

        var account = await gameDbContext.Accounts.SingleAsync();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(registered.AccessToken);

        // DB에는 원문이 아닌 해시만 저장되고, 같은 계정으로 로그인하면 같은 Player 권한 토큰이 나와야 합니다.
        Assert.NotEqual(request.Password, account.PasswordHash);
        Assert.Equal("minwoo_92", account.LoginId);
        Assert.NotNull(loggedIn);
        Assert.Equal(registered.PlayerId, loggedIn.PlayerId);
        Assert.Equal(registered.PlayerId.ToString(), token.Claims
            .Single(claim => claim.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(AccountRole.Player.ToString(), token.Claims
            .Single(claim => claim.Type == ClaimTypes.Role).Value);
    }

    [Fact]
    public async Task LoginWithWrongPasswordReturnsNullInsteadOfToken()
    {
        await using var gameDbContext = new GameDbContext(CreateInMemoryOptions());
        var service = CreateService(gameDbContext);
        await service.RegisterAsync(
            new RegisterAccountRequest("valid_login", "correct-password", "LoginPlayer"),
            CancellationToken.None);

        var result = await service.LoginAsync(
            new LoginRequest("valid_login", "wrong-password"),
            CancellationToken.None);

        // 호출자에게 "계정이 존재한다"는 정보나 토큰을 주지 않고 일반 로그인 실패로 끝내야 합니다.
        Assert.Null(result);
    }

    [Fact]
    public async Task RegisterRejectsShortPasswordBeforeCreatingAnyData()
    {
        await using var gameDbContext = new GameDbContext(CreateInMemoryOptions());
        var service = CreateService(gameDbContext);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterAsync(
                new RegisterAccountRequest("short_password", "short", "NoPlayer"),
                CancellationToken.None));

        // 입력 검증 실패는 Player나 Account 어느 쪽도 중간 저장하지 않아야 합니다.
        Assert.Empty(gameDbContext.Players);
        Assert.Empty(gameDbContext.Accounts);
    }

    /// <summary>테스트마다 독립된 InMemory DB를 만듭니다.</summary>
    private static DbContextOptions<GameDbContext> CreateInMemoryOptions()
    {
        return new DbContextOptionsBuilder<GameDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    /// <summary>실제 해시 구현과 고정된 테스트 전용 JWT 설정을 연결한 서비스를 만듭니다.</summary>
    private static AuthenticationService CreateService(GameDbContext gameDbContext)
    {
        var tokenService = new JwtTokenService(new JwtOptions
        {
            Issuer = "CoopGameServer.UnitTests",
            Audience = "CoopGameServer.UnitTests.Client",
            SigningKey = "unit-test-signing-key-that-is-long-enough-123456",
        });

        return new AuthenticationService(
            gameDbContext,
            new PasswordHasher<Account>(),
            tokenService);
    }
}
