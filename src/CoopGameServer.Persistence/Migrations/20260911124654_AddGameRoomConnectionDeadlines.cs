using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRoomConnectionDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_lifecycle_times",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_wave_state",
                table: "game_rooms");

            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                table: "game_rooms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "initial_connect_deadline",
                table: "game_rooms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_rooms_initial_connect_deadline",
                table: "game_rooms",
                column: "initial_connect_deadline",
                filter: "lifecycle = 0");

            // 기존 진행 방에는 과거 접속 이력이 없습니다. 배포 시점부터 한 번만 입장 유예를 부여합니다.
            // 과거 경기의 승패·체력·시각은 바꾸지 않습니다.
            migrationBuilder.Sql("UPDATE game_rooms SET initial_connect_deadline = CURRENT_TIMESTAMP + INTERVAL '30 seconds' WHERE lifecycle IN (0, 1);");
            migrationBuilder.Sql("UPDATE game_room_players p SET connection_status = 4 FROM game_rooms r WHERE p.room_id = r.room_id AND r.lifecycle = 2;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_lifecycle_times",
                table: "game_rooms",
                sql: "(lifecycle = 0 AND started_at IS NULL AND completed_at IS NULL) OR (lifecycle = 1 AND started_at IS NOT NULL AND completed_at IS NULL) OR (lifecycle = 2 AND completed_at IS NOT NULL AND (started_at IS NOT NULL OR outcome = 3))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_wave_state",
                table: "game_rooms",
                sql: "(lifecycle = 0 AND max_waves = 3 AND current_wave = 0 AND enemy_max_health = 0 AND enemy_current_health = 0 AND enemy_attack_sequence = 0) OR (lifecycle IN (1, 2) AND combat_rule_version > 0 AND max_waves = 3 AND current_wave BETWEEN 1 AND 3 AND enemy_max_health > 0) OR (lifecycle = 2 AND outcome = 3 AND started_at IS NULL AND max_waves = 3 AND current_wave = 0 AND enemy_max_health = 0 AND enemy_current_health = 0 AND enemy_attack_sequence = 0) OR (lifecycle = 2 AND max_waves = 0 AND current_wave = 0 AND enemy_max_health = 0 AND enemy_current_health = 0 AND enemy_attack_sequence = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_game_rooms_initial_connect_deadline",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_lifecycle_times",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_wave_state",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "initial_connect_deadline",
                table: "game_rooms");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_lifecycle_times",
                table: "game_rooms",
                sql: "(lifecycle = 0 AND started_at IS NULL AND completed_at IS NULL) OR (lifecycle = 1 AND started_at IS NOT NULL AND completed_at IS NULL) OR (lifecycle = 2 AND started_at IS NOT NULL AND completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_wave_state",
                table: "game_rooms",
                sql: "(lifecycle = 0 AND max_waves = 3 AND current_wave = 0 AND enemy_max_health = 0 AND enemy_current_health = 0 AND enemy_attack_sequence = 0) OR (lifecycle IN (1, 2) AND combat_rule_version > 0 AND max_waves = 3 AND current_wave BETWEEN 1 AND 3 AND enemy_max_health > 0) OR (lifecycle = 2 AND max_waves = 0 AND current_wave = 0 AND enemy_max_health = 0 AND enemy_current_health = 0 AND enemy_attack_sequence = 0)");
        }
    }
}
