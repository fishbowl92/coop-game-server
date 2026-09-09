using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRoomWaveState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 이전 단계에서도 게임을 시작할 수 있었습니다. 진행 중인 방을 1웨이브로 임의 초기화하지 않습니다.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM game_rooms WHERE lifecycle = 1) THEN
                        RAISE EXCEPTION 'Active rooms must be completed before adding wave state';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM game_rooms r WHERE
                        (SELECT count(*) FROM game_room_players p WHERE p.room_id = r.room_id) <> 4
                    ) OR EXISTS (
                        SELECT 1 FROM game_room_players p JOIN game_rooms r USING (room_id)
                        WHERE p.player_id <> r.player_ids[p.player_order + 1]
                    ) THEN
                        RAISE EXCEPTION 'Invalid room participants before adding wave state';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<int>(
                name: "current_wave",
                table: "game_rooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "enemy_attack_sequence",
                table: "game_rooms",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "enemy_current_health",
                table: "game_rooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "enemy_max_health",
                table: "game_rooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "max_waves",
                table: "game_rooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "state_version",
                table: "game_rooms",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // 과거 완료 방에 없던 웨이브를 만들어 넣지 않습니다. max_waves=0은 상세 없음 표식입니다.
            // 새 state_version=1은 전환 시점의 기준값이며 과거 변경 횟수를 추정한 값이 아닙니다.
            migrationBuilder.Sql("""
                UPDATE game_rooms SET state_version = 1;
                UPDATE game_rooms SET max_waves = 3 WHERE lifecycle = 0;
                ALTER TABLE game_rooms
                    ALTER COLUMN current_wave DROP DEFAULT,
                    ALTER COLUMN max_waves DROP DEFAULT,
                    ALTER COLUMN enemy_max_health DROP DEFAULT,
                    ALTER COLUMN enemy_current_health DROP DEFAULT,
                    ALTER COLUMN state_version DROP DEFAULT,
                    ALTER COLUMN enemy_attack_sequence DROP DEFAULT;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_enemy_attack_sequence",
                table: "game_rooms",
                sql: "enemy_attack_sequence >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_enemy_health",
                table: "game_rooms",
                sql: "enemy_max_health >= 0 AND enemy_current_health BETWEEN 0 AND enemy_max_health");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_state_version",
                table: "game_rooms",
                sql: "state_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_wave_state",
                table: "game_rooms",
                sql: "(lifecycle = 0 AND max_waves = 3 AND current_wave = 0 AND enemy_max_health = 0 AND enemy_current_health = 0 AND enemy_attack_sequence = 0) OR (lifecycle IN (1, 2) AND combat_rule_version > 0 AND max_waves = 3 AND current_wave BETWEEN 1 AND 3 AND enemy_max_health > 0) OR (lifecycle = 2 AND max_waves = 0 AND current_wave = 0 AND enemy_max_health = 0 AND enemy_current_health = 0 AND enemy_attack_sequence = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 이 역변환은 웨이브 상세를 잃으므로 실행 전 별도 승인과 백업이 필요합니다.
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_enemy_attack_sequence",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_enemy_health",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_state_version",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_wave_state",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "current_wave",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "enemy_attack_sequence",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "enemy_current_health",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "enemy_max_health",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "max_waves",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "state_version",
                table: "game_rooms");
        }
    }
}
