using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRoomPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_room_requests",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    request_payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_room_requests", x => x.request_id);
                    table.CheckConstraint("CK_game_room_requests_payload_shape", "(command_kind = 'Create' AND request_payload_json IS NOT NULL) OR (command_kind IN ('Start', 'Complete') AND request_payload_json IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "game_rooms",
                columns: table => new
                {
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    lifecycle = table.Column<int>(type: "integer", nullable: false),
                    party_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    player_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_rooms", x => x.room_id);
                    table.CheckConstraint("CK_game_rooms_four_players", "cardinality(player_ids) = 4");
                    table.CheckConstraint("CK_game_rooms_lifecycle", "lifecycle IN (0, 1, 2)");
                    table.CheckConstraint("CK_game_rooms_lifecycle_times", "(lifecycle = 0 AND started_at IS NULL AND completed_at IS NULL) OR (lifecycle = 1 AND started_at IS NOT NULL AND completed_at IS NULL) OR (lifecycle = 2 AND started_at IS NOT NULL AND completed_at IS NOT NULL)");
                    table.CheckConstraint("CK_game_rooms_party_count", "cardinality(party_ids) <= 4");
                });

            migrationBuilder.CreateIndex(
                name: "IX_game_room_requests_room_id",
                table: "game_room_requests",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "IX_game_rooms_queue_key_lifecycle_created_at",
                table: "game_rooms",
                columns: new[] { "queue_key", "lifecycle", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_room_requests");

            migrationBuilder.DropTable(
                name: "game_rooms");
        }
    }
}
