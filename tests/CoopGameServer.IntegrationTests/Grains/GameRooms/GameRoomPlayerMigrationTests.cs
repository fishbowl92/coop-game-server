using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoopGameServer.IntegrationTests.Grains.GameRooms;

/// <summary>기존 DB 구조에서 새 참가자 구조로 전환할 때 원본 보존과 사전 차단을 검증합니다.</summary>
public sealed class GameRoomPlayerMigrationTests
{
    private const string Baseline = "20260827152943_AddGameResultDeliveryTracking";
    private const string PlayerStateBaseline = "20260909183928_AddGameRoomPlayerState";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WaveMigrationPreservesCompletedRoomWithoutInventingWaveHistory(int combatVersion)
    {
        await using var database = Database();
        await database.StartAsync();
        await using var context = Context(database);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PlayerStateBaseline);
        var roomId = Guid.NewGuid();
        await InsertRoomWithPlayerStateAsync(context, roomId, 2, combatVersion);
        await InsertResultsAsync(context, roomId);
        var originalRoom = await context.Database.SqlQuery<string>($"""
            SELECT to_jsonb(r)::text AS "Value" FROM game_rooms r WHERE room_id = {roomId}
            """).SingleAsync();
        var originalResults = await ReadResultsAsync(context);

        await migrator.MigrateAsync();

        var stored = await context.GameRooms.SingleAsync(r => r.RoomId == roomId);
        Assert.Equal(combatVersion, stored.CombatRuleVersion);
        Assert.Equal(1, stored.StateVersion);
        Assert.Equal(0, stored.MaxWaves);
        Assert.Equal(0, stored.CurrentWave);
        Assert.Equal(0, stored.EnemyMaxHealth);
        Assert.Equal(0, stored.EnemyCurrentHealth);
        Assert.Equal(0, stored.EnemyAttackSequence);
        var preservedRoom = await context.Database.SqlQuery<string>($"""
            SELECT (to_jsonb(r) - ARRAY['current_wave','max_waves','enemy_max_health',
                'enemy_current_health','state_version','enemy_attack_sequence'])::text AS "Value"
            FROM game_rooms r WHERE room_id = {roomId}
            """).SingleAsync();
        Assert.Equal(originalRoom, preservedRoom);
        Assert.Equal(originalResults, await ReadResultsAsync(context));
    }

    [Fact]
    public async Task WaveMigrationBlocksActiveRoomCreatedAfterPlayerMigration()
    {
        await using var database = Database();
        await database.StartAsync();
        await using var context = Context(database);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PlayerStateBaseline);
        await InsertRoomWithPlayerStateAsync(context, Guid.NewGuid(), 1, 1);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.DoesNotContain("AddGameRoomWaveState", string.Join(',', await context.Database.GetAppliedMigrationsAsync()), StringComparison.Ordinal);
        Assert.False(await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
              WHERE table_name = 'game_rooms' AND column_name = 'current_wave') AS "Value"
            """).SingleAsync());
    }

    [Fact]
    public async Task MigrationPreservesCompletedResultsAndBackfillsOrderedParticipants()
    {
        await using var database = Database();
        await database.StartAsync();
        await using var context = Context(database);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Baseline);
        var readyId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var players = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await InsertRoomAsync(context, readyId, players, 0, 0);
        await InsertRoomAsync(context, completedId, players, 2, 1);
        await InsertResultsAsync(context, completedId);
        var payload = "{\"outcome\":\"Victory\"}";
        var response = "{}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO game_room_requests
              (room_id, request_id, command_kind, request_payload_json, result_payload_json, created_at)
            VALUES ({completedId}, {Guid.NewGuid()}, 'Complete', {payload}::jsonb, {response}::jsonb, now());
            """);
        var originalResults = await ReadResultsAsync(context);
        var originalRequests = await ReadRequestsAsync(context);
        var originalRoom = await context.Database.SqlQuery<string>($"""
            SELECT row_to_json(r)::text AS "Value" FROM game_rooms r WHERE room_id = {completedId}
            """).SingleAsync();

        await migrator.MigrateAsync();

        var ready = await context.GameRooms.SingleAsync(room => room.RoomId == readyId);
        var completed = await context.GameRooms.SingleAsync(room => room.RoomId == completedId);
        Assert.Equal(1, ready.CombatRuleVersion);
        Assert.Equal(0, completed.CombatRuleVersion);
        Assert.Equal(1, completed.Outcome);
        Assert.Equal(7, completed.RewardPolicyVersion);
        foreach (var roomId in new[] { readyId, completedId })
        {
            var records = await context.GameRoomPlayers.Where(p => p.RoomId == roomId).OrderBy(p => p.PlayerOrder).ToArrayAsync();
            Assert.Equal(players, records.Select(p => p.PlayerId));
            Assert.Equal(Enumerable.Range(0, 4), records.Select(p => p.PlayerOrder));
        }

        Assert.Equal(originalResults, await ReadResultsAsync(context));
        Assert.Equal(originalRequests, await ReadRequestsAsync(context));
        // 추가된 버전 열을 제외한 기존 방의 모든 필드를 비교합니다.
        var preservedRoom = await context.Database.SqlQuery<string>($"""
            SELECT row_to_json(r)::text AS "Value" FROM
              (SELECT room_id, queue_key, lifecycle, party_ids, player_ids, created_at, started_at,
                      completed_at, outcome, reward_policy_version FROM game_rooms WHERE room_id = {completedId}) r
            """).SingleAsync();
        // JSON 프로퍼티 순서와 무관하게 각 원래 필드의 값이 그대로인지 비교합니다.
        using var before = System.Text.Json.JsonDocument.Parse(originalRoom);
        using var after = System.Text.Json.JsonDocument.Parse(preservedRoom);
        foreach (var field in before.RootElement.EnumerateObject())
        {
            Assert.Equal(field.Value.GetRawText(), after.RootElement.GetProperty(field.Name).GetRawText());
        }
    }

    [Theory]
    [InlineData("active")]
    [InlineData("duplicate-player")]
    [InlineData("missing-result")]
    public async Task MigrationRejectsUnsafeLegacyDataWithoutPartialSchema(string scenario)
    {
        await using var database = Database();
        await database.StartAsync();
        await using var context = Context(database);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Baseline);
        var roomId = Guid.NewGuid();
        var players = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        if (scenario == "duplicate-player")
        {
            players[3] = players[0];
        }

        var lifecycle = scenario == "active" ? 1 : scenario == "missing-result" ? 2 : 0;
        await InsertRoomAsync(context, roomId, players, lifecycle, lifecycle == 2 ? 1 : 0);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.DoesNotContain("AddGameRoomPlayerState", string.Join(',', await context.Database.GetAppliedMigrationsAsync()), StringComparison.Ordinal);
        Assert.False(await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
              WHERE table_name = 'game_rooms' AND column_name = 'combat_rule_version') AS "Value"
            """).SingleAsync());
        Assert.False(await context.Database.SqlQuery<bool>($"""
            SELECT to_regclass('public.game_room_players') IS NOT NULL AS "Value"
            """).SingleAsync());
    }

    /// <summary>테스트마다 독립된 컨테이너를 만들어 구형 스키마 검증이 다른 테스트를 방해하지 않게 합니다.</summary>
    private static PostgreSqlContainer Database() => new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("migration_test").WithUsername("migration_test").WithPassword("temporary-test-password").Build();

    private static GameDbContext Context(PostgreSqlContainer database) => new(
        new DbContextOptionsBuilder<GameDbContext>().UseNpgsql(database.GetConnectionString()).Options);

    /// <summary>참가자 단계까지 적용됐지만 웨이브 열은 없는 DB에 기존 방·참가자 상태를 준비합니다.</summary>
    private static async Task InsertRoomWithPlayerStateAsync(GameDbContext context, Guid roomId, int lifecycle, int combatVersion)
    {
        var players = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO game_rooms (room_id, queue_key, lifecycle, party_ids, player_ids, created_at,
                started_at, completed_at, outcome, reward_policy_version, combat_rule_version)
            VALUES ({roomId}, 'coop-dungeon-normal-v1', {lifecycle}, ARRAY[]::uuid[], {players}, now(), now(),
                CASE WHEN {lifecycle} = 2 THEN now() END, CASE WHEN {lifecycle} = 2 THEN 1 ELSE 0 END, 7, {combatVersion});
            INSERT INTO game_room_players (room_id, player_id, player_order, max_health, current_health,
                combat_status, last_command_sequence, basic_attack_ready_at, skill_ready_at)
            SELECT {roomId}, p.id, (p.n-1)::integer, 100, 100, 0, 0, NULL, NULL
            FROM unnest({players}) WITH ORDINALITY p(id, n);
            """);
    }

    /// <summary>구형 스키마에는 신규 열이 없으므로 최신 EF 모델 대신 명시적인 SQL로 준비합니다.</summary>
    private static Task<int> InsertRoomAsync(GameDbContext context, Guid roomId, Guid[] players, int lifecycle, int outcome) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO game_rooms (room_id, queue_key, lifecycle, party_ids, player_ids, created_at,
                started_at, completed_at, outcome, reward_policy_version)
            VALUES ({roomId}, 'coop-dungeon-normal-v1', {lifecycle}, ARRAY[]::uuid[], {players}, now(),
                CASE WHEN {lifecycle} > 0 THEN now() END,
                CASE WHEN {lifecycle} = 2 THEN now() END, {outcome}, 7)
            """);

    /// <summary>보상 정책·각 전달 상태·시도 횟수·오류·재시도 시각이 있는 과거 결과를 준비합니다.</summary>
    private static Task<int> InsertResultsAsync(GameDbContext context, Guid roomId) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO game_results (room_id, player_id, reward_policy_version, reward_request_id,
                delivery_status, attempt_count, next_attempt_at, last_error_code, updated_at)
            SELECT r.room_id, p.id, 7, gen_random_uuid(), p.n::integer, p.n::integer,
                CASE WHEN p.n = 1 THEN now() + interval '1 hour' END,
                CASE WHEN p.n IN (1, 4) THEN 'test-error' END, now()
            FROM game_rooms r CROSS JOIN LATERAL unnest(r.player_ids) WITH ORDINALITY p(id, n)
            WHERE r.room_id = {roomId}
            """);

    private static Task<string[]> ReadResultsAsync(GameDbContext context) => context.Database.SqlQuery<string>($"""
        SELECT row_to_json(g)::text AS "Value" FROM game_results g ORDER BY player_id
        """).ToArrayAsync();

    private static Task<string[]> ReadRequestsAsync(GameDbContext context) => context.Database.SqlQuery<string>($"""
        SELECT row_to_json(r)::text AS "Value" FROM game_room_requests r ORDER BY request_id
        """).ToArrayAsync();
}
