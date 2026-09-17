using System.Net.Http.Headers;
using CoopGameServer.Contracts.Administration;
using CoopGameServer.Contracts.Authentication;
using CoopGameServer.Contracts.Players;
using CoopGameServer.Contracts.Rewards;

namespace CoopGameServer.Admin.Services;

/// <summary>관리 화면이 데이터베이스나 Grain에 직접 접근하지 않도록 모든 작업을 HTTP API로 제한합니다.</summary>
public sealed class AdminApiClient(IHttpClientFactory httpClientFactory, AdminSession session)
{
    public async Task SignInAsync(string loginId, string password, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient("GameApi");
        using var response = await client.PostAsJsonAsync(
            "api/auth/login",
            new LoginRequest(loginId, password),
            cancellationToken);
        var authentication = await ReadAsync<AuthenticationResponse>(response, cancellationToken);
        if (!string.Equals(authentication.Role, "Administrator", StringComparison.Ordinal))
        {
            throw new AdminApiException("관리자 역할이 없는 계정입니다.", System.Net.HttpStatusCode.Forbidden);
        }

        session.SignIn(authentication.LoginId, authentication.AccessToken, authentication.ExpiresAt);
    }

    public Task<AdminPlayerLookupResponse> LookupPlayerAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        GetAsync<AdminPlayerLookupResponse>(
            $"api/admin/players/lookup?query={Uri.EscapeDataString(query)}",
            cancellationToken);

    public Task<PlayerProgressionResponse> GetProgressionAsync(
        Guid playerId,
        CancellationToken cancellationToken = default) =>
        GetAsync<PlayerProgressionResponse>(
            $"api/players/{playerId}/progression?pageSize=100",
            cancellationToken);

    public Task<AdminRewardHistoryResponse[]> GetRewardHistoryAsync(
        Guid playerId,
        CancellationToken cancellationToken = default) =>
        GetAsync<AdminRewardHistoryResponse[]>(
            $"api/admin/players/{playerId}/reward-history?limit=20",
            cancellationToken);

    public async Task<GrantRewardResponse> GrantRewardAsync(
        Guid playerId,
        GrantRewardRequest request,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateAuthorizedClient();
        using var response = await client.PostAsJsonAsync(
            $"api/players/{playerId}/rewards",
            request,
            cancellationToken);
        return await ReadAsync<GrantRewardResponse>(response, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var client = CreateAuthorizedClient();
        using var response = await client.GetAsync(path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private HttpClient CreateAuthorizedClient()
    {
        if (!session.IsAuthenticated || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new AdminApiException("관리자 로그인이 필요합니다.", System.Net.HttpStatusCode.Unauthorized);
        }

        var client = httpClientFactory.CreateClient("GameApi");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new AdminApiException("API 응답 본문이 비어 있습니다.", response.StatusCode);
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AdminApiException(
            string.IsNullOrWhiteSpace(detail) ? $"API 요청 실패: {(int)response.StatusCode}" : detail,
            response.StatusCode);
    }
}

public sealed class AdminApiException(string message, System.Net.HttpStatusCode statusCode) : Exception(message)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
