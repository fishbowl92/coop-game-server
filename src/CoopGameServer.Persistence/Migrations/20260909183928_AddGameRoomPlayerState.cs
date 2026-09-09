using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRoomPlayerState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 구조 변경 전에 검사합니다. 진행 중인 방의 전투 경과를 추측하거나 자동 취소하지 않습니다.
            // 실패하면 EF Core의 마이그레이션 트랜잭션 전체가 취소됩니다.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM game_rooms WHERE lifecycle = 1) THEN
                        RAISE EXCEPTION 'Active legacy rooms must be completed before adding player state';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM game_rooms r
                        WHERE cardinality(r.player_ids) <> 4
                           OR (SELECT count(DISTINCT id) FROM unnest(r.player_ids) id) <> 4
                           OR array_position(r.player_ids, NULL) IS NOT NULL
                           OR '00000000-0000-0000-0000-000000000000'::uuid = ANY(r.player_ids)
                    ) THEN
                        RAISE EXCEPTION 'Invalid legacy room participants';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM game_rooms r WHERE r.lifecycle = 2 AND
                        (SELECT count(*) FROM game_results g WHERE g.room_id = r.room_id) <> 4
                    ) OR EXISTS (
                        SELECT 1 FROM game_results g JOIN game_rooms r USING (room_id)
                        WHERE r.lifecycle <> 2 OR NOT (g.player_id = ANY(r.player_ids))
                           OR g.reward_policy_version <> r.reward_policy_version
                    ) THEN
                        RAISE EXCEPTION 'Invalid legacy room reward delivery records';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<int>(
                name: "combat_rule_version",
                table: "game_rooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "game_room_players",
                columns: table => new
                {
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_order = table.Column<int>(type: "integer", nullable: false),
                    max_health = table.Column<int>(type: "integer", nullable: false),
                    current_health = table.Column<int>(type: "integer", nullable: false),
                    combat_status = table.Column<int>(type: "integer", nullable: false),
                    last_command_sequence = table.Column<long>(type: "bigint", nullable: false),
                    basic_attack_ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    skill_ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_room_players", x => new { x.room_id, x.player_id });
                    table.CheckConstraint("CK_game_room_players_combat_status", "(current_health > 0 AND combat_status = 0) OR (current_health = 0 AND combat_status = 1)");
                    table.CheckConstraint("CK_game_room_players_health", "max_health > 0 AND current_health BETWEEN 0 AND max_health");
                    table.CheckConstraint("CK_game_room_players_order", "player_order BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_game_room_players_sequence", "last_command_sequence >= 0");
                    table.ForeignKey(
                        name: "FK_game_room_players_game_rooms_room_id",
                        column: x => x.room_id,
                        principalTable: "game_rooms",
                        principalColumn: "room_id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Ready는 새 규칙의 초기 상태로 전환합니다. 완료 방은 버전 0으로 과거 기록임을 표시합니다.
            // 승패·보상 정책·game_results·요청 JSON에는 손대지 않습니다.
            migrationBuilder.Sql("""
                UPDATE game_rooms SET combat_rule_version = 1 WHERE lifecycle = 0;
                ALTER TABLE game_rooms ALTER COLUMN combat_rule_version DROP DEFAULT;
                INSERT INTO game_room_players
                    (room_id, player_id, player_order, max_health, current_health, combat_status,
                     last_command_sequence, basic_attack_ready_at, skill_ready_at)
                SELECT r.room_id, p.player_id, (p.position - 1)::integer, 100, 100, 0, 0, NULL, NULL
                FROM game_rooms r
                CROSS JOIN LATERAL unnest(r.player_ids) WITH ORDINALITY AS p(player_id, position);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_combat_rule_version",
                table: "game_rooms",
                sql: "combat_rule_version > 0 OR (combat_rule_version = 0 AND lifecycle = 2)");

            migrationBuilder.CreateIndex(
                name: "IX_game_room_players_room_id_player_order",
                table: "game_room_players",
                columns: new[] { "room_id", "player_order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 역방향 적용은 새 참가자 상세를 잃습니다. 운영 롤백은 별도 백업·승인 절차가 필요합니다.
            migrationBuilder.DropTable(
                name: "game_room_players");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_combat_rule_version",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "combat_rule_version",
                table: "game_rooms");
        }
    }
}
