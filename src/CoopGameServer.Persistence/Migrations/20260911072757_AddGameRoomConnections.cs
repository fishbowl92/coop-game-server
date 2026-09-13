using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRoomConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "abandoned_at",
                table: "game_room_players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "connection_generation",
                table: "game_room_players",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "connection_id",
                table: "game_room_players",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "connection_status",
                table: "game_room_players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "disconnected_at",
                table: "game_room_players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "game_room_players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "game_room_players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reconnect_deadline",
                table: "game_room_players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_room_players_lease_expires_at",
                table: "game_room_players",
                column: "lease_expires_at",
                filter: "connection_status = 1");

            migrationBuilder.CreateIndex(
                name: "IX_game_room_players_reconnect_deadline",
                table: "game_room_players",
                column: "reconnect_deadline",
                filter: "connection_status = 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_room_players_connection",
                table: "game_room_players",
                sql: "connection_generation >= 0 AND (\n  (connection_status = 0 AND connection_generation = 0\n    AND connection_id IS NULL AND last_seen_at IS NULL AND lease_expires_at IS NULL\n    AND disconnected_at IS NULL AND reconnect_deadline IS NULL AND abandoned_at IS NULL)\n  OR (connection_status = 1 AND connection_generation > 0\n    AND connection_id IS NOT NULL AND connection_id <> '00000000-0000-0000-0000-000000000000'::uuid\n    AND last_seen_at IS NOT NULL AND lease_expires_at IS NOT NULL AND last_seen_at < lease_expires_at\n    AND disconnected_at IS NULL AND reconnect_deadline IS NULL AND abandoned_at IS NULL)\n  OR (connection_status = 2 AND connection_generation > 0 AND connection_id IS NULL\n    AND last_seen_at IS NOT NULL AND lease_expires_at IS NOT NULL\n    AND disconnected_at IS NOT NULL AND reconnect_deadline IS NOT NULL\n    AND last_seen_at <= disconnected_at AND lease_expires_at = disconnected_at\n    AND disconnected_at < reconnect_deadline AND abandoned_at IS NULL)\n  OR (connection_status = 3 AND connection_id IS NULL AND abandoned_at IS NOT NULL AND (\n    (connection_generation = 0 AND last_seen_at IS NULL AND lease_expires_at IS NULL\n      AND disconnected_at IS NULL AND reconnect_deadline IS NULL)\n    OR (connection_generation > 0 AND last_seen_at IS NOT NULL AND lease_expires_at IS NOT NULL\n      AND disconnected_at IS NOT NULL AND reconnect_deadline IS NOT NULL\n      AND last_seen_at <= disconnected_at AND lease_expires_at = disconnected_at\n      AND disconnected_at < reconnect_deadline AND reconnect_deadline <= abandoned_at)))\n  OR (connection_status = 4 AND connection_id IS NULL AND lease_expires_at IS NULL)\n)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_game_room_players_lease_expires_at",
                table: "game_room_players");

            migrationBuilder.DropIndex(
                name: "IX_game_room_players_reconnect_deadline",
                table: "game_room_players");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_room_players_connection",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "abandoned_at",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "connection_generation",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "connection_id",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "connection_status",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "disconnected_at",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "game_room_players");

            migrationBuilder.DropColumn(
                name: "reconnect_deadline",
                table: "game_room_players");
        }
    }
}
