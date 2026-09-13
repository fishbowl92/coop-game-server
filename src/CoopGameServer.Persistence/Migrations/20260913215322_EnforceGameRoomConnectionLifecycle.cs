using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceGameRoomConnectionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_cancellation_reason",
                table: "game_rooms",
                sql: "cancellation_reason IS NULL OR (lifecycle = 2 AND outcome = 3 AND started_at IS NULL AND cancellation_reason = 'InitialConnectionTimeout')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_rooms_initial_connect_deadline",
                table: "game_rooms",
                sql: "lifecycle = 2 OR initial_connect_deadline IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_cancellation_reason",
                table: "game_rooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_rooms_initial_connect_deadline",
                table: "game_rooms");
        }
    }
}
