using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeMatchmakingRequestIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_match_queue_requests",
                table: "match_queue_requests");

            migrationBuilder.DropIndex(
                name: "IX_match_queue_requests_queue_key",
                table: "match_queue_requests");

            migrationBuilder.DropPrimaryKey(
                name: "PK_game_room_requests",
                table: "game_room_requests");

            migrationBuilder.DropIndex(
                name: "IX_game_room_requests_room_id",
                table: "game_room_requests");

            migrationBuilder.AddPrimaryKey(
                name: "PK_match_queue_requests",
                table: "match_queue_requests",
                columns: new[] { "queue_key", "request_id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_game_room_requests",
                table: "game_room_requests",
                columns: new[] { "room_id", "request_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_match_queue_requests",
                table: "match_queue_requests");

            migrationBuilder.DropPrimaryKey(
                name: "PK_game_room_requests",
                table: "game_room_requests");

            migrationBuilder.AddPrimaryKey(
                name: "PK_match_queue_requests",
                table: "match_queue_requests",
                column: "request_id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_game_room_requests",
                table: "game_room_requests",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_queue_requests_queue_key",
                table: "match_queue_requests",
                column: "queue_key");

            migrationBuilder.CreateIndex(
                name: "IX_game_room_requests_room_id",
                table: "game_room_requests",
                column: "room_id");
        }
    }
}
