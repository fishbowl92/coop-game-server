using CoopGameServer.Domain.Accounts;
using CoopGameServer.Domain.Players;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Api.Application.Administration;

/// <summary>User Secrets가 명시한 경우에만 로컬 개발용 관리자 계정을 최초 한 번 생성합니다.</summary>
public sealed class DevelopmentAdministratorBootstrap(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DevelopmentAdministratorBootstrap> logger) : IHostedService
{
    private const string SectionName = "DevelopmentAdministrator";
    private static readonly Action<ILogger, string, Exception?> AdministratorCreatedLog =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, nameof(AdministratorCreatedLog)),
            "Development administrator {LoginId} was created.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var loginId = configuration[$"{SectionName}:LoginId"];
        var password = configuration[$"{SectionName}:Password"];
        var nickname = configuration[$"{SectionName}:Nickname"];

        // 세 값 중 하나라도 없으면 기능을 사용하지 않는 구성입니다. 기본 비밀번호를 만들지 않습니다.
        if (string.IsNullOrWhiteSpace(loginId) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(nickname))
        {
            return;
        }

        if (password.Length < 8)
        {
            throw new InvalidOperationException("DevelopmentAdministrator:Password는 8자 이상이어야 합니다.");
        }

        var normalizedLoginId = Account.NormalizeLoginId(loginId);
        using var scope = scopeFactory.CreateScope();
        var gameDbContext = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>();
        var existingAccount = await gameDbContext.Accounts
            .SingleOrDefaultAsync(account => account.LoginId == normalizedLoginId, cancellationToken);

        if (existingAccount is not null)
        {
            if (existingAccount.Role is not AccountRole.Administrator)
            {
                // 기존 일반 계정을 구성만으로 승격하면 누가 권한을 바꿨는지 추적하기 어렵습니다.
                throw new InvalidOperationException(
                    "DevelopmentAdministrator LoginId가 일반 Player 계정에 이미 사용 중입니다. 자동 승격하지 않습니다.");
            }

            return;
        }

        if (await gameDbContext.Players.AnyAsync(player => player.Nickname == nickname.Trim(), cancellationToken))
        {
            throw new InvalidOperationException(
                "DevelopmentAdministrator Nickname이 기존 Player에 사용 중입니다. 기존 Player를 관리자에 연결하지 않습니다.");
        }

        var now = DateTimeOffset.UtcNow;
        var player = new Player(Guid.NewGuid(), nickname, now);
        var account = new Account(Guid.NewGuid(), player.Id, normalizedLoginId, AccountRole.Administrator, now);
        account.SetPasswordHash(passwordHasher.HashPassword(account, password));
        gameDbContext.Players.Add(player);
        gameDbContext.Accounts.Add(account);
        await gameDbContext.SaveChangesAsync(cancellationToken);

        // 비밀번호는 기록하지 않고 생성된 로그인 식별자만 운영 로그에 남깁니다.
        AdministratorCreatedLog(logger, account.LoginId, null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
