using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "parties",
                columns: table => new
                {
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lifecycle = table.Column<int>(type: "integer", nullable: false),
                    leader_player_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parties", x => x.party_id);
                });

            migrationBuilder.CreateTable(
                name: "party_requests",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_error = table.Column<int>(type: "integer", nullable: false),
                    result_lifecycle = table.Column<int>(type: "integer", nullable: true),
                    result_leader_player_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_member_player_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_requests", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "party_members",
                columns: table => new
                {
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    join_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_members", x => new { x.party_id, x.player_id });
                    table.CheckConstraint("CK_party_members_join_order_nonnegative", "join_order >= 0");
                    table.ForeignKey(
                        name: "FK_party_members_parties_party_id",
                        column: x => x.party_id,
                        principalTable: "parties",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_party_members_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "player_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_party_members_party_id_join_order",
                table: "party_members",
                columns: ["party_id", "join_order"]);

            migrationBuilder.CreateIndex(
                name: "IX_party_members_player_id",
                table: "party_members",
                column: "player_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_party_requests_party_id",
                table: "party_requests",
                column: "party_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_members");

            migrationBuilder.DropTable(
                name: "party_requests");

            migrationBuilder.DropTable(
                name: "parties");
        }
    }
}
