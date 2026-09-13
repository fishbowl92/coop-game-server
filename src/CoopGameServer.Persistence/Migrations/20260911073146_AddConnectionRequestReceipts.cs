using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionRequestReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests",
                sql: "(command_kind IN ('Create', 'Complete') AND request_payload_json IS NOT NULL AND player_id IS NULL AND accepted_command_sequence IS NULL) OR (command_kind = 'Start' AND request_payload_json IS NULL AND player_id IS NULL AND accepted_command_sequence IS NULL) OR (command_kind = 'Connection' AND request_payload_json IS NOT NULL AND player_id IS NOT NULL AND accepted_command_sequence IS NULL) OR (command_kind IN ('BasicAttack', 'UseSkill') AND request_payload_json IS NOT NULL AND player_id IS NOT NULL AND (accepted_command_sequence IS NULL OR accepted_command_sequence > 0))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests",
                sql: "(command_kind IN ('Create', 'Complete') AND request_payload_json IS NOT NULL AND player_id IS NULL AND accepted_command_sequence IS NULL) OR (command_kind = 'Start' AND request_payload_json IS NULL AND player_id IS NULL AND accepted_command_sequence IS NULL) OR (command_kind IN ('BasicAttack', 'UseSkill') AND request_payload_json IS NOT NULL AND player_id IS NOT NULL AND (accepted_command_sequence IS NULL OR accepted_command_sequence > 0))");
        }
    }
}
