using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchmakingPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "match_queue_requests",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    command_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    request_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_queue_requests", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "match_queue_tickets",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entry_kind = table.Column<int>(type: "integer", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    leader_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    queue_order = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_queue_tickets", x => x.ticket_id);
                    table.CheckConstraint("CK_match_queue_tickets_entry_kind", "entry_kind IN (0, 1)");
                    table.CheckConstraint("CK_match_queue_tickets_entry_shape", "(entry_kind = 0 AND party_id IS NOT NULL) OR (entry_kind = 1 AND party_id IS NULL)");
                    table.CheckConstraint("CK_match_queue_tickets_queue_order_positive", "queue_order > 0");
                    table.CheckConstraint("CK_match_queue_tickets_status", "status IN (0, 1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "match_queue_members",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_queue_members", x => new { x.ticket_id, x.player_id });
                    table.CheckConstraint("CK_match_queue_members_order_nonnegative", "member_order >= 0");
                    table.ForeignKey(
                        name: "FK_match_queue_members_match_queue_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "match_queue_tickets",
                        principalColumn: "ticket_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_match_queue_members_ticket_id_member_order",
                table: "match_queue_members",
                columns: new[] { "ticket_id", "member_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_queue_requests_queue_key",
                table: "match_queue_requests",
                column: "queue_key");

            migrationBuilder.CreateIndex(
                name: "IX_match_queue_tickets_queue_key_party_id",
                table: "match_queue_tickets",
                columns: new[] { "queue_key", "party_id" });

            migrationBuilder.CreateIndex(
                name: "IX_match_queue_tickets_queue_key_queue_order",
                table: "match_queue_tickets",
                columns: new[] { "queue_key", "queue_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_queue_tickets_queue_key_status_queue_order",
                table: "match_queue_tickets",
                columns: new[] { "queue_key", "status", "queue_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_queue_members");

            migrationBuilder.DropTable(
                name: "match_queue_requests");

            migrationBuilder.DropTable(
                name: "match_queue_tickets");
        }
    }
}
