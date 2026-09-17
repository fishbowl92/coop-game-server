using CoopGameServer.Domain.Administration;

namespace CoopGameServer.UnitTests.Domain.Administration;

public sealed class AdminAuditTests
{
    [Fact]
    public void ConstructorNormalizesReasonAndPreservesActorAndReward()
    {
        var administratorAccountId = Guid.NewGuid();
        var targetPlayerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var audit = new AdminAudit(
            Guid.NewGuid(),
            administratorAccountId,
            targetPlayerId,
            requestId,
            AdminAuditAction.GrantReward,
            300,
            42,
            2,
            "  customer-support-recovery  ",
            AdminAuditResult.Applied,
            DateTimeOffset.UtcNow);

        Assert.Equal(administratorAccountId, audit.AdministratorAccountId);
        Assert.Equal(targetPlayerId, audit.TargetPlayerId);
        Assert.Equal(requestId, audit.RequestId);
        Assert.Equal(300, audit.GoldAmount);
        Assert.Equal(42, audit.ItemId);
        Assert.Equal(2, audit.ItemQuantity);
        Assert.Equal("customer-support-recovery", audit.Reason);
    }

    [Theory]
    [InlineData(nameof(AdminAudit.Id))]
    [InlineData(nameof(AdminAudit.AdministratorAccountId))]
    [InlineData(nameof(AdminAudit.TargetPlayerId))]
    [InlineData(nameof(AdminAudit.RequestId))]
    public void ConstructorRejectsEmptyRequiredId(string emptyProperty)
    {
        var id = Guid.NewGuid();
        var administratorAccountId = Guid.NewGuid();
        var targetPlayerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        if (emptyProperty == nameof(AdminAudit.Id)) id = Guid.Empty;
        if (emptyProperty == nameof(AdminAudit.AdministratorAccountId)) administratorAccountId = Guid.Empty;
        if (emptyProperty == nameof(AdminAudit.TargetPlayerId)) targetPlayerId = Guid.Empty;
        if (emptyProperty == nameof(AdminAudit.RequestId)) requestId = Guid.Empty;

        Assert.Throws<ArgumentException>(() => new AdminAudit(
            id,
            administratorAccountId,
            targetPlayerId,
            requestId,
            AdminAuditAction.GrantReward,
            1,
            null,
            null,
            "valid-reason",
            AdminAuditResult.Applied,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ConstructorRejectsUnsupportedActionAndResult()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            AdminAuditAction.None,
            AdminAuditResult.Applied));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            AdminAuditAction.GrantReward,
            AdminAuditResult.None));
    }

    private static AdminAudit Create(AdminAuditAction action, AdminAuditResult result) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            action,
            1,
            null,
            null,
            "valid-reason",
            result,
            DateTimeOffset.UtcNow);
}
