using CoopGameServer.Domain.Accounts;

namespace CoopGameServer.UnitTests.Domain.Accounts;

/// <summary>Account가 로그인 식별자를 일관된 형태로 저장하는지 검증합니다.</summary>
public sealed class AccountTests
{
    [Fact]
    public void ConstructorNormalizesLoginIdToTrimmedLowercaseValue()
    {
        var account = new Account(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "  Minwoo_92  ",
            AccountRole.Player,
            DateTimeOffset.UtcNow);

        // 대소문자를 달리 입력해도 DB UNIQUE 인덱스가 같은 계정으로 판단하도록 소문자 저장을 고정합니다.
        Assert.Equal("minwoo_92", account.LoginId);
        Assert.Equal(AccountRole.Player, account.Role);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("contains-hyphen")]
    [InlineData("contains space")]
    [InlineData("한글로그인")]
    public void ConstructorRejectsLoginIdOutsideAllowedFormat(string loginId)
    {
        Assert.Throws<ArgumentException>(
            () => new Account(
                Guid.NewGuid(),
                Guid.NewGuid(),
                loginId,
                AccountRole.Player,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetPasswordHashRejectsWhitespaceOnlyValue()
    {
        var account = new Account(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "valid_login",
            AccountRole.Player,
            DateTimeOffset.UtcNow);

        // PasswordHash가 비어 있으면 로그인 검증이 무의미해지므로 엔티티 수준에서도 저장을 막습니다.
        Assert.Throws<ArgumentException>(() => account.SetPasswordHash("   "));
    }
}
