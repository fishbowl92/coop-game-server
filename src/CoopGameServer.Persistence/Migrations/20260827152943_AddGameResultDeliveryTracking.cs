using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameResultDeliveryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_results",
                columns: table => new
                {
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reward_policy_version = table.Column<int>(type: "integer", nullable: false),
                    reward_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_status = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_results", x => new { x.room_id, x.player_id });
                    table.CheckConstraint("CK_game_results_attempt_count_nonnegative", "attempt_count >= 0");
                    table.CheckConstraint("CK_game_results_delivery_status", "delivery_status IN (0, 1, 2, 3, 4)");
                    table.CheckConstraint("CK_game_results_reward_policy_version_positive", "reward_policy_version > 0");
                    table.ForeignKey(
                        name: "FK_game_results_game_rooms_room_id",
                        column: x => x.room_id,
                        principalTable: "game_rooms",
                        principalColumn: "room_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_game_results_delivery_status_next_attempt_at_room_id",
                table: "game_results",
                columns: new[] { "delivery_status", "next_attempt_at", "room_id" });

            migrationBuilder.CreateIndex(
                name: "IX_game_results_reward_request_id",
                table: "game_results",
                column: "reward_request_id",
                unique: true);

            // 기존 Completed 방도 복구 서비스가 빠짐없이 찾을 수 있도록 네 참가자의 Pending 행을 만듭니다.
            // pgcrypto의 SHA-256 결과 앞 16바이트를 UUID로 바꾸며, 새 코드의 CreateRewardRequestId와
            // 같은 문자열·해시·바이트 순서를 사용하므로 재시작 뒤에도 같은 멱등성 키가 계산됩니다.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
            migrationBuilder.Sql(
                "WITH completed_results AS ("
                + " SELECT room.room_id, player.player_id, room.reward_policy_version, room.completed_at,"
                + " encode(digest(convert_to("
                + " 'game-room-reward-v1:' || room.room_id::text || ':' || player.player_id::text || ':'"
                + " || room.reward_policy_version::text, 'UTF8'), 'sha256'), 'hex') AS reward_hash"
                + " FROM game_rooms AS room"
                + " CROSS JOIN LATERAL unnest(room.player_ids) AS player(player_id)"
                + " WHERE room.lifecycle = 2"
                + ")"
                + " INSERT INTO game_results ("
                + " room_id, player_id, reward_policy_version, reward_request_id, delivery_status,"
                + " attempt_count, next_attempt_at, last_error_code, updated_at)"
                + " SELECT room_id, player_id, reward_policy_version,"
                + " (substring(reward_hash FROM 1 FOR 8) || '-'"
                + " || substring(reward_hash FROM 9 FOR 4) || '-'"
                + " || substring(reward_hash FROM 13 FOR 4) || '-'"
                + " || substring(reward_hash FROM 17 FOR 4) || '-'"
                + " || substring(reward_hash FROM 21 FOR 12))::uuid,"
                + " 0, 0, NULL, NULL, completed_at"
                + " FROM completed_results;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_results");
        }
    }
}
