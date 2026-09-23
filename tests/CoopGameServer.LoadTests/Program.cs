using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoopGameServer.Contracts.Authentication;
using CoopGameServer.Contracts.Matchmaking;
using CoopGameServer.Contracts.Players;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

var options = LoadOptions.FromEnvironment();
Directory.CreateDirectory(options.ReportDirectory);
using var http = new HttpClient
{
    BaseAddress = new Uri(options.BaseUrl),
    Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds),
};
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var samples = new ConcurrentQueue<LoadSample>();
var suffix = Guid.NewGuid().ToString("N")[..8];
var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
var clients = new List<LoadPlayer>();
var rooms = new List<LoadRoom>();
var requestIndex = -1;
DateTimeOffset loadStartedAt;

// 준비 요청은 성능 표본에 넣지 않습니다. 실제 인증·매칭·접속 경로로만 데이터를 만듭니다.
if (options.Scenario == "progression")
{
    for (var i = 0; i < 8; i++)
        clients.Add(await RegisterAsync(i));
    foreach (var client in clients)
    {
        var response = await GetAsync<PlayerProgressionResponse>(
            $"api/players/{client.PlayerId}/progression?pageSize=20", client);
        if (response.PlayerId != client.PlayerId)
            throw new InvalidOperationException("진행도 예열 응답의 PlayerId가 다릅니다.");
    }
}
else
{
    var roomCount = (int)Math.Ceiling(options.Rate * options.DurationSeconds / 4.0) + 1;
    if (roomCount > 40)
        throw new InvalidOperationException("전투 방 40개를 초과합니다. 실험 규모를 줄이세요.");
    for (var roomNumber = 0; roomNumber < roomCount; roomNumber++)
    {
        var players = new LoadPlayer[4];
        for (var playerNumber = 0; playerNumber < 4; playerNumber++)
        {
            var client = await RegisterAsync(roomNumber * 4 + playerNumber);
            clients.Add(client);
            players[playerNumber] = client;
        }
        Guid roomId = default;
        foreach (var client in players)
        {
            var response = await PostAsync<MatchmakingResponse>(
                "api/matchmaking/queues/coop-dungeon-normal-v1/solo",
                new { requestId = Guid.NewGuid() }, client);
            if (response.Match is { } match)
                roomId = match.RoomId;
        }
        if (roomId == Guid.Empty)
            throw new InvalidOperationException("네 명의 매칭에서 게임 방이 생성되지 않았습니다.");
        rooms.Add(new LoadRoom(roomId, players));
    }

    // 연결 유효기간은 15초입니다. 모든 방의 매칭을 끝낸 다음 접속·시작합니다.
    foreach (var room in rooms)
    {
        foreach (var client in room.Players)
        {
            var connected = await PostAsync<GameRoomConnectionResult>(
                $"api/game-rooms/{room.RoomId}/connect",
                new { requestId = Guid.NewGuid() }, client);
            if (connected.Error != "None" || connected.ConnectionId is null)
                throw new InvalidOperationException($"게임 방 접속 실패: {connected.Error}");
            client.ConnectionId = connected.ConnectionId.Value;
            client.Generation = connected.Generation;
        }
        var leader = room.Players[0];
        var started = await PostAsync<GameRoomConnectionResult>(
            $"api/game-rooms/{room.RoomId}/start-combat",
            new { requestId = Guid.NewGuid(), leader.ConnectionId, leader.Generation }, leader);
        if (started.Error != "None")
            throw new InvalidOperationException($"전투 시작 실패: {started.Error}");
    }
}

// 각 주입 건은 고유한 계정 또는 방·참가자 쌍을 고른다. 전투 참가자는 한 번만 공격하여
// 플레이어별 순번 1, 재사용 대기시간, 방별 최대 행동 수를 동시에 지킨다.
loadStartedAt = DateTimeOffset.UtcNow;
var scenario = Scenario.Create(options.Scenario, async context =>
{
    var ordinal = Interlocked.Increment(ref requestIndex);
    var watch = Stopwatch.StartNew();
    string classification;
    int statusCode;
    try
    {
        if (options.Scenario == "progression")
        {
            var client = clients[ordinal % clients.Count];
            using var response = await SendAsync(HttpMethod.Get,
                $"api/players/{client.PlayerId}/progression?pageSize=20", null, client);
            statusCode = (int)response.StatusCode;
            var body = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<PlayerProgressionResponse>(json)
                : null;
            classification = response.IsSuccessStatusCode && body?.PlayerId == client.PlayerId
                ? "success" : ClassifyFailure(response.StatusCode);
        }
        else
        {
            var roomIndex = ordinal / 4;
            if (roomIndex >= rooms.Count)
                throw new InvalidOperationException("계산한 방 수보다 많은 명령이 주입됐습니다.");
            var room = rooms[roomIndex];
            var client = room.Players[ordinal % 4];
            using var response = await SendAsync(HttpMethod.Post,
                $"api/game-rooms/{room.RoomId}/use-skill",
                new { requestId = Guid.NewGuid(), client.ConnectionId, client.Generation, sequence = 1 },
                client);
            statusCode = (int)response.StatusCode;
            var body = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<GameRoomCombatResult>(json)
                : null;
            classification = response.IsSuccessStatusCode
                && body?.Error == GameRoomCombatError.None && body.IsReplay == false
                ? "success" : ClassifyFailure(response.StatusCode);
        }
    }
    catch (Exception exception)
    {
        statusCode = 0;
        classification = exception.GetType().Name;
    }
    watch.Stop();
    samples.Enqueue(new LoadSample(ordinal, statusCode, classification, watch.Elapsed.TotalMilliseconds));
    return classification == "success" ? Response.Ok() : Response.Fail();
}).WithoutWarmUp().WithLoadSimulations(
    Simulation.Inject(rate: options.Rate, interval: TimeSpan.FromSeconds(1),
        during: TimeSpan.FromSeconds(options.DurationSeconds)));

NBomberRunner.RegisterScenarios(scenario)
    .WithReportFolder(options.ReportDirectory)
    .WithReportFormats(ReportFormat.Csv, ReportFormat.Md)
    .Run();

var ordered = samples.OrderBy(sample => sample.Ordinal).ToArray();
var success = ordered.Count(sample => sample.Classification == "success");
var sorted = ordered.Where(sample => sample.Classification == "success")
    .Select(sample => sample.ElapsedMs).OrderBy(value => value).ToArray();
double Percentile(double value) => sorted.Length == 0 ? 0
    : sorted[(int)Math.Ceiling(value * sorted.Length) - 1];
var summary = new
{
    options.Scenario,
    options.Rate,
    options.DurationSeconds,
    options.HttpTimeoutSeconds,
    LoadStartedAt = loadStartedAt,
    GitCommit = options.GitCommit,
    DotnetRuntime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    Environment = Environment.OSVersion.ToString(),
    ProcessorCount = Environment.ProcessorCount,
    PreparedPlayers = clients.Count,
    PreparedRooms = rooms.Count,
    Requests = ordered.Length,
    SuccessfulRequests = success,
    UnexpectedResponses = ordered.Length - success,
    SuccessRate = ordered.Length == 0 ? 0 : success / (double)ordered.Length,
    P50Ms = Percentile(0.50),
    P95Ms = Percentile(0.95),
    P99Ms = Percentile(0.99),
    ValidRequestsPerSecond = success / (double)options.DurationSeconds,
};
await File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, "summary.json"),
    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
await File.WriteAllLinesAsync(Path.Combine(options.ReportDirectory, "samples.csv"),
    ["ordinal,status_code,classification,elapsed_ms",
     ..ordered.Select(sample => string.Join(",", sample.Ordinal,
         sample.StatusCode, sample.Classification,
         sample.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture)))]);

// 읽기 전용 검사는 HTTP 성공률과 독립적으로 실행한다. 영속 행과 응답 수의 불일치를 숨기지 않는다.
var dbOptions = new DbContextOptionsBuilder<GameDbContext>()
    .UseNpgsql(options.DbConnection).Options;
await using var db = new GameDbContext(dbOptions);
var playerIds = clients.Select(client => client.PlayerId).ToArray();
var persistedPlayers = await db.Players.AsNoTracking().CountAsync(player => playerIds.Contains(player.Id));
if (persistedPlayers != clients.Count)
    throw new InvalidOperationException("준비된 플레이어의 영속 행 수가 다릅니다.");
if (await db.PlayerWallets.AsNoTracking().AnyAsync(wallet =>
        playerIds.Contains(wallet.PlayerId) && wallet.Gold < 0))
    throw new InvalidOperationException("음수 골드가 발견됐습니다.");
if (await db.InventoryItems.AsNoTracking().AnyAsync(item =>
        playerIds.Contains(item.PlayerId) && item.Quantity < 0))
    throw new InvalidOperationException("음수 인벤토리가 발견됐습니다.");

if (options.Scenario == "progression")
{
    foreach (var client in clients)
    {
        var response = await GetAsync<PlayerProgressionResponse>(
            $"api/players/{client.PlayerId}/progression?pageSize=20", client);
        var gold = await db.PlayerWallets.AsNoTracking()
            .Where(wallet => wallet.PlayerId == client.PlayerId)
            .Select(wallet => (long?)wallet.Gold).SingleOrDefaultAsync() ?? 0;
        var items = await db.InventoryItems.AsNoTracking()
            .Where(item => item.PlayerId == client.PlayerId)
            .OrderBy(item => item.ItemId)
            .Select(item => new { item.ItemId, item.Quantity }).ToArrayAsync();
        if (response.Gold != gold
            || !response.Items.OrderBy(item => item.ItemId)
                .Select(item => (item.ItemId, item.Quantity))
                .SequenceEqual(items.Select(item => (item.ItemId, item.Quantity))))
            throw new InvalidOperationException("진행도 캐시와 DB의 재화·인벤토리가 다릅니다.");
    }
}
else
{
    var roomIds = rooms.Select(room => room.RoomId).ToArray();
    var persistedRooms = await db.GameRooms.AsNoTracking()
        .Where(room => roomIds.Contains(room.RoomId)).ToArrayAsync();
    var persistedParticipants = await db.GameRoomPlayers.AsNoTracking()
        .Where(player => roomIds.Contains(player.RoomId)).ToArrayAsync();
    var accepted = await db.GameRoomRequests.AsNoTracking()
        .Where(request => roomIds.Contains(request.RoomId)
            && request.CommandKind == "UseSkill" && request.AcceptedCommandSequence != null)
        .ToArrayAsync();
    if (persistedRooms.Length != rooms.Count || persistedParticipants.Length != rooms.Count * 4)
        throw new InvalidOperationException("게임 방 또는 참가자 영속 행 수가 다릅니다.");
    if (await db.GameResults.AsNoTracking().AnyAsync(result => roomIds.Contains(result.RoomId))
        || await db.RewardAudits.AsNoTracking().AnyAsync(audit => playerIds.Contains(audit.PlayerId)))
        throw new InvalidOperationException("미완료 부하 시험 방에 결과·보상이 생성됐습니다.");
    if (accepted.Length != success
        || accepted.Select(request => request.RequestId).Distinct().Count() != accepted.Length)
        throw new InvalidOperationException("성공 응답과 영속 전투 명령 수가 다릅니다.");
    foreach (var player in persistedParticipants)
    {
        var commands = accepted.Where(request => request.RoomId == player.RoomId
            && request.PlayerId == player.PlayerId)
            .Select(request => request.AcceptedCommandSequence!.Value)
            .OrderBy(sequence => sequence).ToArray();
        if (player.LastCommandSequence != commands.Length
            || !commands.SequenceEqual(Enumerable.Range(1, commands.Length).Select(value => (long)value)))
            throw new InvalidOperationException("참가자별 전투 명령 순번이 연속적이지 않습니다.");
    }
    foreach (var room in persistedRooms)
    {
        if (room.PlayerIds.Distinct().Count() != 4
            || persistedParticipants.Count(player => player.RoomId == room.RoomId) != 4)
            throw new InvalidOperationException("방 참가자 네 명 불변식이 깨졌습니다.");
    }
}
await File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, "invariants.txt"),
    $"PASS: {DateTimeOffset.UtcNow:O}; scenario={options.Scenario}; players={clients.Count}; rooms={rooms.Count}{Environment.NewLine}");
Console.WriteLine($"Week 8 {options.Scenario}: {success}/{ordered.Length} valid, p95={Percentile(0.95):F1} ms, invariants PASS.");
if (success != ordered.Length || ordered.Length == 0 || Percentile(0.95) > 2000)
    Environment.ExitCode = 1;

string ClassifyFailure(HttpStatusCode status) => status == HttpStatusCode.Conflict
    ? "business_rejection" : status == HttpStatusCode.OK
        ? "invalid_body" : "unexpected_http";

async Task<LoadPlayer> RegisterAsync(int index)
{
    var loginId = $"load_{suffix}_{index}";
    var account = await PostAsync<AuthenticationResponse>("api/auth/register",
        new RegisterAccountRequest(loginId, secret, $"Load{suffix}{index}"), null);
    return new LoadPlayer(account.PlayerId, account.AccessToken);
}

async Task<T> GetAsync<T>(string path, LoadPlayer client)
{
    using var response = await SendAsync(HttpMethod.Get, path, null, client);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"준비 GET 실패: {path}, HTTP {(int)response.StatusCode}");
    return await response.Content.ReadFromJsonAsync<T>(json)
        ?? throw new InvalidOperationException("준비 GET 응답이 비어 있습니다.");
}

async Task<T> PostAsync<T>(string path, object body, LoadPlayer? client)
{
    using var response = await SendAsync(HttpMethod.Post, path, body, client);
    if (!response.IsSuccessStatusCode)
    {
        var problem = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"준비 POST 실패: {path}, HTTP {(int)response.StatusCode}, {problem}");
    }
    return await response.Content.ReadFromJsonAsync<T>(json)
        ?? throw new InvalidOperationException("준비 POST 응답이 비어 있습니다.");
}

async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, LoadPlayer? client)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = JsonContent.Create(body, options: json);
    if (client is not null)
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", client.Token);
    return await http.SendAsync(request);
}

internal sealed class LoadPlayer(Guid playerId, string token)
{
    public Guid PlayerId { get; } = playerId;
    public string Token { get; } = token;
    public Guid ConnectionId { get; set; }
    public long Generation { get; set; }
}

internal sealed record LoadRoom(Guid RoomId, LoadPlayer[] Players);
internal sealed record LoadSample(int Ordinal, int StatusCode, string Classification, double ElapsedMs);

internal sealed record LoadOptions(
    string Scenario, string BaseUrl, string DbConnection, string ReportDirectory,
    int Rate, int DurationSeconds, int HttpTimeoutSeconds, string GitCommit)
{
    public static LoadOptions FromEnvironment()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} 환경 변수가 필요합니다.");
        int Positive(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (raw is null) return fallback;
            if (!int.TryParse(raw, out var value) || value <= 0)
                throw new ArgumentOutOfRangeException(name, "양수여야 합니다.");
            return value;
        }
        var scenario = Required("COOP_LOAD_SCENARIO");
        if (scenario is not ("progression" or "combat"))
            throw new ArgumentException("COOP_LOAD_SCENARIO는 progression 또는 combat입니다.");
        return new(scenario, Required("COOP_LOAD_BASE_URL"), Required("COOP_LOAD_DB_CONNECTION"),
            Required("COOP_LOAD_REPORT_DIR"), Positive("COOP_LOAD_RATE", 1),
            Positive("COOP_LOAD_DURATION_SECONDS", 8),
            Positive("COOP_LOAD_HTTP_TIMEOUT_SECONDS", 10), Required("COOP_LOAD_GIT_COMMIT"));
    }
}
