using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCombatRequestPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests");

            migrationBuilder.AddColumn<long>(
                name: "accepted_command_sequence",
                table: "game_room_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "player_id",
                table: "game_room_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_room_requests_room_id_player_id_accepted_command_seque~",
                table: "game_room_requests",
                columns: new[] { "room_id", "player_id", "accepted_command_sequence" },
                unique: true,
                filter: "accepted_command_sequence IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests",
                sql: "(command_kind IN ('Create', 'Complete') AND request_payload_json IS NOT NULL AND player_id IS NULL AND accepted_command_sequence IS NULL) OR (command_kind = 'Start' AND request_payload_json IS NULL AND player_id IS NULL AND accepted_command_sequence IS NULL) OR (command_kind IN ('BasicAttack', 'UseSkill') AND request_payload_json IS NOT NULL AND player_id IS NOT NULL AND (accepted_command_sequence IS NULL OR accepted_command_sequence > 0))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_game_room_requests_room_id_player_id_accepted_command_seque~",
                table: "game_room_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests");

            migrationBuilder.DropColumn(
                name: "accepted_command_sequence",
                table: "game_room_requests");

            migrationBuilder.DropColumn(
                name: "player_id",
                table: "game_room_requests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_room_requests_payload_shape",
                table: "game_room_requests",
                sql: "(command_kind IN ('Create', 'Complete') AND request_payload_json IS NOT NULL) OR (command_kind = 'Start' AND request_payload_json IS NULL)");
        }
    }
}
