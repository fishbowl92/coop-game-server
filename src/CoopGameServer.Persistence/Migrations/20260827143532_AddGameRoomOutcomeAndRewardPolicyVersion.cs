using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRoomOutcomeAndRewardPolicyVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests");

            migrationBuilder.AddColumn<int>(
                name: "outcome",
                table: "game_rooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reward_policy_version",
                table: "game_rooms",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // 이전 버전에서 이미 완료된 방은 실제 승패를 복원할 자료가 없습니다.
            // 사실과 다른 Victory/Defeat를 추측하지 않고 운영상 중립적인 Cancelled로 이관합니다.
            migrationBuilder.Sql(
                "UPDATE game_rooms SET outcome = 3 WHERE lifecycle = 2;");

            // 이전 Complete 요청은 매개변수가 없어 JSON 본문이 null이었습니다.
            // 새 멱등성 규칙은 결과까지 비교하므로, 기존 완료 방과 같은 Cancelled 본문을 보충합니다.
            migrationBuilder.Sql(
                "UPDATE game_room_requests "
                + "SET request_payload_json = '{\"outcome\":\"Cancelled\"}'::jsonb "
                + "WHERE command_kind = 'Complete' AND request_payload_json IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_lifecycle_outcome",
                table: "game_rooms",
                sql: "(lifecycle IN (0, 1) AND outcome = 0) OR (lifecycle = 2 AND outcome IN (1, 2, 3))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_outcome",
                table: "game_rooms",
                sql: "outcome IN (0, 1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_reward_policy_version_positive",
                table: "game_rooms",
                sql: "reward_policy_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests",
                sql: "(command_kind IN ('Create', 'Complete') AND request_payload_json IS NOT NULL) OR (command_kind = 'Start' AND request_payload_json IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_lifecycle_outcome",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_outcome",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_reward_policy_version_positive",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "game_rooms");

            migrationBuilder.DropColumn(
                name: "reward_policy_version",
                table: "game_rooms");

            // 이전 스키마에서는 Complete 요청 본문이 반드시 null이어야 합니다.
            migrationBuilder.Sql(
                "UPDATE game_room_requests SET request_payload_json = NULL "
                + "WHERE command_kind = 'Complete';");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests",
                sql: "(command_kind = 'Create' AND request_payload_json IS NOT NULL) OR (command_kind IN ('Start', 'Complete') AND request_payload_json IS NULL)");
        }
    }
}
