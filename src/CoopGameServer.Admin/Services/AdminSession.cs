namespace CoopGameServer.Admin.Services;

/// <summary>한 Blazor Server 회로 안에서만 관리자 접근 토큰을 보관합니다.</summary>
public sealed class AdminSession
{
    public string? AccessToken { get; private set; }
    public string? LoginId { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAt > DateTimeOffset.UtcNow;

    public void SignIn(string loginId, string accessToken, DateTimeOffset expiresAt)
    {
        LoginId = loginId;
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
    }

    public void SignOut()
    {
        LoginId = null;
        AccessToken = null;
        ExpiresAt = null;
    }
}
