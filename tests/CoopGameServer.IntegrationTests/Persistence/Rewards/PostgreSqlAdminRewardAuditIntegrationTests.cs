using CoopGameServer.Domain.Accounts;
using CoopGameServer.Domain.Administration;
using CoopGameServer.Domain.Players;
using CoopGameServer.IntegrationTests.Infrastructure;
using CoopGameServer.Persistence.Rewards;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.IntegrationTests.PersistenceLayer.Rewards;

/// <summary>관리자 보상 효과와 두 감사 기록이 하나의 PostgreSQL 트랜잭션으로 움직이는지 검증합니다.</summary>
[Collection(PostgreSqlIntegrationTestGroup.Name)]
public sealed class PostgreSqlAdminRewardAuditIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlDatabaseFixture _databaseFixture;
    private readonly PostgreSqlRewardWriter _writer;

    public PostgreSqlAdminRewardAuditIntegrationTests(PostgreSqlDatabaseFixture databaseFixture)
    {
        _databaseFixture = databaseFixture;
        _writer = new PostgreSqlRewardWriter(databaseFixture, TimeProvider.System);
    }

    public Task InitializeAsync() => _databaseFixture.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task WriteAsyncAtomicallyStoresRewardEffectAndAdministratorAudit()
    {
        var (targetPlayerId, administratorAccountId) = await CreateTargetAndAdministratorAsync();
        var command = new RewardWriteCommand(
            Guid.NewGuid(), targetPlayerId, 700, 5001, 3, "support-recovery", administratorAccountId);

        var result = await _writer.WriteAsync(command);

        Assert.Equal(RewardWriteError.None, result.Error);
        Assert.False(result.IsReplay);
        await using var db = _databaseFixture.CreateDbContext();
        Assert.Equal(700, (await db.PlayerWallets.SingleAsync(x => x.PlayerId == targetPlayerId)).Gold);
        Assert.Equal(3, (await db.InventoryItems.SingleAsync(x =>
            x.PlayerId == targetPlayerId && x.ItemId == 5001)).Quantity);
        var rewardAudit = await db.RewardAudits.SingleAsync(x => x.RequestId == command.RequestId);
        var adminAudit = await db.AdminAudits.SingleAsync(x => x.RequestId == command.RequestId);
        Assert.Equal(rewardAudit.PlayerId, adminAudit.TargetPlayerId);
        Assert.Equal(administratorAccountId, adminAudit.AdministratorAccountId);
        Assert.Equal(AdminAuditAction.GrantReward, adminAudit.Action);
        Assert.Equal(AdminAuditResult.Applied, adminAudit.Result);
    }

    [Fact]
    public async Task WriteAsyncReplaysOnlyForSameAdministratorAndPayload()
    {
        var (targetPlayerId, administratorAccountId) = await CreateTargetAndAdministratorAsync();
        var secondAdministratorAccountId = await CreateAdministratorAsync("second_admin");
        var command = new RewardWriteCommand(
            Guid.NewGuid(), targetPlayerId, 25, null, null, "manual-grant", administratorAccountId);

        var first = await _writer.WriteAsync(command);
        var replay = await _writer.WriteAsync(command);
        var actorConflict = await _writer.WriteAsync(
            command with { AdministratorAccountId = secondAdministratorAccountId });

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(RewardWriteError.IdempotencyConflict, actorConflict.Error);
        await using var db = _databaseFixture.CreateDbContext();
        Assert.Equal(25, (await db.PlayerWallets.SingleAsync(x => x.PlayerId == targetPlayerId)).Gold);
        Assert.Equal(1, await db.RewardAudits.CountAsync(x => x.RequestId == command.RequestId));
        Assert.Equal(1, await db.AdminAudits.CountAsync(x => x.RequestId == command.RequestId));
    }

    [Fact]
    public async Task WriteAsyncRejectsNonAdministratorWithoutAnyRewardEffectOrAudit()
    {
        var (targetPlayerId, _) = await CreateTargetAndAdministratorAsync();
        var ordinaryAccountId = await CreateAccountAsync("ordinary_player", AccountRole.Player);
        var requestId = Guid.NewGuid();

        var result = await _writer.WriteAsync(new RewardWriteCommand(
            requestId, targetPlayerId, 100, null, null, "must-be-rejected", ordinaryAccountId));

        Assert.Equal(RewardWriteError.InvalidAdministrator, result.Error);
        await using var db = _databaseFixture.CreateDbContext();
        Assert.False(await db.PlayerWallets.AnyAsync(x => x.PlayerId == targetPlayerId));
        Assert.False(await db.RewardAudits.AnyAsync(x => x.RequestId == requestId));
        Assert.False(await db.AdminAudits.AnyAsync(x => x.RequestId == requestId));
    }

    [Fact]
    public async Task WriteAsyncForMissingTargetRollsBackBothAudits()
    {
        var administratorAccountId = await CreateAdministratorAsync("missing_target_admin");
        var requestId = Guid.NewGuid();

        var result = await _writer.WriteAsync(new RewardWriteCommand(
            requestId, Guid.NewGuid(), 100, null, null, "missing-target", administratorAccountId));

        Assert.Equal(RewardWriteError.PlayerNotFound, result.Error);
        await using var db = _databaseFixture.CreateDbContext();
        Assert.False(await db.RewardAudits.AnyAsync(x => x.RequestId == requestId));
        Assert.False(await db.AdminAudits.AnyAsync(x => x.RequestId == requestId));
    }

    private async Task<(Guid TargetPlayerId, Guid AdministratorAccountId)> CreateTargetAndAdministratorAsync()
    {
        var targetPlayerId = Guid.NewGuid();
        await using (var db = _databaseFixture.CreateDbContext())
        {
            db.Players.Add(new Player(targetPlayerId, "AuditTarget", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        return (targetPlayerId, await CreateAdministratorAsync("audit_admin"));
    }

    private Task<Guid> CreateAdministratorAsync(string loginId) =>
        CreateAccountAsync(loginId, AccountRole.Administrator);

    private async Task<Guid> CreateAccountAsync(string loginId, AccountRole role)
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var account = new Account(accountId, playerId, loginId, role, now);
        account.SetPasswordHash("integration-test-password-hash");

        await using var db = _databaseFixture.CreateDbContext();
        db.Players.Add(new Player(playerId, $"P{playerId:N}"[..Player.MaxNicknameLength], now));
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return accountId;
    }
}
