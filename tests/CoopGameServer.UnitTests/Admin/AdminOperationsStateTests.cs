using System.Net;
using System.Net.Http.Json;
using CoopGameServer.Admin.Components.Administration;
using CoopGameServer.Admin.Services;
using CoopGameServer.Contracts.Administration;
using CoopGameServer.Contracts.Authentication;
using CoopGameServer.Contracts.Players;
using CoopGameServer.Contracts.Rewards;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoopGameServer.UnitTests.Admin;

/// <summary>실제 AdminApiClient에 HTTP 실패를 주입해 화면의 조회·지급·재시도 흐름을 검증합니다.</summary>
public sealed class AdminOperationsStateTests
{
    [Theory]
    [InlineData("/lookup")]
    [InlineData("/progression")]
    [InlineData("/reward-history")]
    public async Task FailedPlayerSwitchClearsOldSelectionAndBlocksGrant(string failingPath)
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        Assert.Equal(scenario.PlayerA.PlayerId, scenario.State.Selection!.Player.PlayerId);
        scenario.Handler.Respond = request => Task.FromResult(
            request.Path.EndsWith(failingPath, StringComparison.Ordinal)
                ? Failure(HttpStatusCode.ServiceUnavailable) : scenario.Read(request));

        await scenario.State.LookupAsync("B");
        await scenario.State.GrantAsync();

        Assert.Null(scenario.State.Selection);
        Assert.False(scenario.State.CanGrant);
        Assert.Equal("error", scenario.State.MessageStyle);
        Assert.DoesNotContain(scenario.Handler.Requests, request => request.Reward is not null);
    }

    [Theory]
    [InlineData("/progression")]
    [InlineData("/reward-history")]
    public async Task ConfirmedGrantSurvivesRefreshFailureAndRefreshNeverPostsAgain(string failingPath)
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        var firstId = scenario.Reward.RequestId;
        scenario.Handler.Respond = request => Task.FromResult(request.Reward is not null
            ? scenario.Receipt(request)
            : request.Path.EndsWith(failingPath, StringComparison.Ordinal)
                ? Failure(HttpStatusCode.ServiceUnavailable) : scenario.Read(request));

        await scenario.State.GrantAsync();

        Assert.Equal(firstId, scenario.State.LastReceipt!.RequestId);
        Assert.NotEqual(firstId, scenario.Reward.RequestId);
        Assert.Null(scenario.Reward.Pending);
        Assert.True(scenario.State.RequiresRefresh);
        Assert.False(scenario.State.CanGrant);
        Assert.Equal("warning", scenario.State.MessageStyle);
        Assert.Contains("보상 지급은 완료", scenario.State.Message, StringComparison.Ordinal);
        await scenario.State.GrantAsync();
        Assert.Single(scenario.Handler.Requests, request => request.Reward is not null);

        scenario.Handler.Respond = request => Task.FromResult(scenario.Read(request));
        await scenario.State.RefreshAsync();

        Assert.False(scenario.State.RequiresRefresh);
        Assert.True(scenario.State.CanGrant);
        Assert.Equal(firstId, scenario.State.LastReceipt.RequestId);
        Assert.Single(scenario.Handler.Requests, request => request.Reward is not null);
    }

    [Fact]
    public async Task LostResponseRetriesOriginalPayloadAndBlocksTargetChange()
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        scenario.Handler.Respond = _ => throw new HttpRequestException("response lost");
        await scenario.State.GrantAsync();
        var pending = Assert.IsType<PendingAdminReward>(scenario.Reward.Pending);
        var requestCount = scenario.Handler.Requests.Count;
        scenario.Reward.GoldAmount = 999;
        scenario.Reward.Reason = "edited";
        await scenario.State.LookupAsync("B");

        Assert.Equal(requestCount, scenario.Handler.Requests.Count);
        Assert.False(scenario.State.CanEditReward);
        scenario.Handler.Respond = request => Task.FromResult(
            request.Reward is not null ? scenario.Receipt(request, replay: true) : scenario.Read(request));
        await scenario.State.GrantAsync();

        var sent = scenario.Handler.Requests.Where(request => request.Reward is not null).ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(sent[0].Path, sent[1].Path);
        Assert.Equal(sent[0].Reward, sent[1].Reward);
        Assert.Equal(100, sent[1].Reward!.GoldAmount);
        Assert.Equal(pending.Request.RequestId, scenario.State.LastReceipt!.RequestId);
        Assert.True(scenario.State.LastReceipt.IsReplay);
        Assert.Null(scenario.Reward.Pending);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task FirstDefiniteRejectionAllowsCorrection(HttpStatusCode status)
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        scenario.Handler.Respond = _ => Task.FromResult(Failure(status));

        await scenario.State.GrantAsync();

        Assert.Null(scenario.Reward.Pending);
        Assert.Null(scenario.State.LastReceipt);
        Assert.True(scenario.State.CanEditReward);
        Assert.True(scenario.State.CanLookup);
        Assert.Equal(100, scenario.Reward.GoldAmount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task RejectionAfterLostResponseCannotDiscardOriginalRequest(HttpStatusCode status)
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        scenario.Handler.Respond = _ => throw new HttpRequestException("response lost");
        await scenario.State.GrantAsync();
        var pending = scenario.Reward.Pending;
        scenario.Handler.Respond = _ => Task.FromResult(Failure(status));

        await scenario.State.GrantAsync();

        Assert.Same(pending, scenario.Reward.Pending);
        Assert.False(scenario.State.CanEditReward);
        Assert.False(scenario.State.CanLookup);
        Assert.Null(scenario.State.LastReceipt);
    }

    [Fact]
    public async Task InFlightGrantBlocksDuplicateClickLookupAndLogout()
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource<ObservedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.Respond = request =>
        {
            if (request.Reward is null) return Task.FromResult(scenario.Read(request));
            posted.SetResult(request);
            return response.Task;
        };

        var grant = scenario.State.GrantAsync();
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scenario.State.IsBusy);
        await scenario.State.GrantAsync();
        await scenario.State.LookupAsync("B");
        scenario.State.SignOut();
        Assert.True(scenario.Session.IsAuthenticated);
        Assert.False(scenario.State.CanEditReward);
        Assert.False(scenario.State.CanRefresh);
        var sent = Assert.Single(scenario.Handler.Requests, request => request.Reward is not null);
        response.SetResult(scenario.Receipt(sent));
        await grant;

        Assert.False(scenario.State.IsBusy);
        Assert.NotNull(scenario.State.LastReceipt);
        Assert.Equal(scenario.PlayerA.PlayerId, scenario.State.Selection!.Player.PlayerId);
    }

    [Fact]
    public async Task PendingRequestRequiresOriginalAdministratorAndCanResumeAfterSignOut()
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        scenario.Handler.Respond = _ => throw new HttpRequestException("response lost");
        await scenario.State.GrantAsync();
        var pending = scenario.Reward.Pending;
        scenario.State.SignOut();
        scenario.Handler.Respond = _ => Task.FromResult(Json(new AuthenticationResponse(
            Guid.NewGuid(), "other-admin", "Administrator", "unit-test-placeholder", DateTimeOffset.UtcNow.AddHours(1))));
        await scenario.State.SignInAsync("other-admin", "unit-test-password");
        var count = scenario.Handler.Requests.Count;

        await scenario.State.GrantAsync();

        Assert.False(scenario.State.CanGrant);
        Assert.Equal(count, scenario.Handler.Requests.Count);
        Assert.Same(pending, scenario.Reward.Pending);
        scenario.Handler.Respond = _ => Task.FromResult(Json(new AuthenticationResponse(
            Guid.NewGuid(), "admin", "Administrator", "unit-test-placeholder", DateTimeOffset.UtcNow.AddHours(1))));
        await scenario.State.SignInAsync("admin", "unit-test-password");
        scenario.Handler.Respond = request => Task.FromResult(
            request.Reward is not null ? scenario.Receipt(request, replay: true) : scenario.Read(request));
        await scenario.State.GrantAsync();

        Assert.True(scenario.State.LastReceipt!.IsReplay);
        Assert.Null(scenario.Reward.Pending);
        Assert.Equal(scenario.PlayerA.PlayerId, scenario.State.Selection!.Player.PlayerId);
    }

    [Fact]
    public async Task MismatchedSuccessReceiptKeepsRequestUnconfirmed()
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        scenario.Handler.Respond = request => Task.FromResult(Json(
            new GrantRewardResponse(Guid.NewGuid(), Guid.NewGuid(), scenario.PlayerA.PlayerId,
                100, null, null, "support", DateTimeOffset.UtcNow, false)));

        await scenario.State.GrantAsync();

        Assert.NotNull(scenario.Reward.Pending);
        Assert.Null(scenario.State.LastReceipt);
        Assert.Equal("error", scenario.State.MessageStyle);
    }

    [Fact]
    public async Task RenderedPageKeepsConfirmedReceiptBesideRefreshWarning()
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        scenario.Handler.Respond = request => Task.FromResult(request.Reward is not null
            ? scenario.Receipt(request) : Failure(HttpStatusCode.ServiceUnavailable));
        await scenario.State.GrantAsync();

        var html = await RenderAsync(scenario);

        Assert.Contains("보상 지급 완료", html, StringComparison.Ordinal);
        Assert.Contains(scenario.State.LastReceipt!.RequestId.ToString(), html, StringComparison.Ordinal);
        Assert.Contains("보상 지급은 완료됐지만", html, StringComparison.Ordinal);
        Assert.Contains("<fieldset disabled", html, StringComparison.Ordinal);
        Assert.Contains("정보 새로고침", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderedPendingRequestShowsFrozenPayloadWithoutEditableRewardForm()
    {
        using var scenario = new Scenario();
        await scenario.State.LookupAsync("A");
        scenario.Handler.Respond = _ => throw new HttpRequestException("response lost");
        await scenario.State.GrantAsync();
        scenario.Reward.GoldAmount = 999;

        var html = await RenderAsync(scenario);

        Assert.Contains("같은 요청으로 결과 확인", html, StringComparison.Ordinal);
        Assert.Contains("골드 100", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<fieldset", html, StringComparison.Ordinal);
        Assert.Contains(scenario.Reward.Pending!.Request.RequestId.ToString()!, html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(Scenario scenario)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(scenario.Session);
        services.AddSingleton(scenario.Reward);
        services.AddSingleton(scenario.State);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<AdminOperationsPanel>(ParameterView.Empty);
            return WebUtility.HtmlDecode(rendered.ToHtmlString());
        });
    }

    private static HttpResponseMessage Failure(HttpStatusCode status) =>
        new(status) { Content = new StringContent("controlled failure") };

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Scenario : IDisposable
    {
        public AdminSession Session { get; } = new();
        public AdminRewardSubmissionState Reward { get; } = new() { GoldAmount = 100, Reason = " support " };
        public ScriptedHandler Handler { get; } = new();
        public AdminOperationsState State { get; }
        public AdminPlayerLookupResponse PlayerA { get; } = Player("A");
        public AdminPlayerLookupResponse PlayerB { get; } = Player("B");

        public Scenario()
        {
            SignIn("admin");
            Handler.Respond = request => Task.FromResult(Read(request));
            State = new AdminOperationsState(new AdminApiClient(new ClientFactory(Handler), Session), Session, Reward);
        }

        public void SignIn(string loginId) =>
            Session.SignIn(loginId, "unit-test-placeholder", DateTimeOffset.UtcNow.AddHours(1));

        public HttpResponseMessage Read(ObservedRequest request)
        {
            var player = request.Path.Contains(PlayerB.PlayerId.ToString(), StringComparison.Ordinal) ||
                request.Query.Contains("query=B", StringComparison.Ordinal) ? PlayerB : PlayerA;
            if (request.Path.EndsWith("/lookup", StringComparison.Ordinal)) return Json(player);
            if (request.Path.EndsWith("/progression", StringComparison.Ordinal))
            {
                return Json(new PlayerProgressionResponse(
                    player.PlayerId, player.Nickname, player.CreatedAt, player.UpdatedAt,
                    player == PlayerA ? 900 : 200, [], null));
            }

            if (request.Path.EndsWith("/reward-history", StringComparison.Ordinal))
                return Json(Array.Empty<AdminRewardHistoryResponse>());
            throw new InvalidOperationException($"Unexpected request: {request.Path}");
        }

        public HttpResponseMessage Receipt(ObservedRequest request, bool replay = false)
        {
            var reward = request.Reward!;
            return Json(new GrantRewardResponse(Guid.NewGuid(), reward.RequestId!.Value, PlayerA.PlayerId,
                reward.GoldAmount, reward.ItemId, reward.ItemQuantity, reward.Reason!, DateTimeOffset.UtcNow, replay));
        }

        public void Dispose() => Handler.Dispose();

        private static AdminPlayerLookupResponse Player(string name) =>
            new(Guid.NewGuid(), name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private sealed record ObservedRequest(string Path, string Query, GrantRewardRequest? Reward);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Func<ObservedRequest, Task<HttpResponseMessage>> Respond { get; set; } =
            _ => throw new InvalidOperationException("No response configured.");
        public List<ObservedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/rewards", StringComparison.Ordinal)
                ? await request.Content!.ReadFromJsonAsync<GrantRewardRequest>(cancellationToken)
                : null;
            var observed = new ObservedRequest(request.RequestUri!.AbsolutePath, request.RequestUri.Query, body);
            Requests.Add(observed);
            return await Respond(observed);
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://admin-unit.test/") };
    }
}
