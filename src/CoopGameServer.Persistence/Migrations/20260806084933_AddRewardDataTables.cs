using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRewardDataTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventories",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventories", x => new { x.player_id, x.item_id });
                    table.CheckConstraint("CK_inventories_item_id_positive", "item_id > 0");
                    table.CheckConstraint("CK_inventories_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_inventories_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "player_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "player_wallets",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gold = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_wallets", x => x.player_id);
                    table.CheckConstraint("CK_player_wallets_gold_nonnegative", "gold >= 0");
                    table.ForeignKey(
                        name: "FK_player_wallets_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "player_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reward_audits",
                columns: table => new
                {
                    reward_audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gold_amount = table.Column<long>(type: "bigint", nullable: false),
                    item_id = table.Column<int>(type: "integer", nullable: true),
                    item_quantity = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reward_audits", x => x.reward_audit_id);
                    table.CheckConstraint("CK_reward_audits_gold_nonnegative", "gold_amount >= 0");
                    table.CheckConstraint("CK_reward_audits_has_reward", "gold_amount > 0 OR item_id IS NOT NULL");
                    table.CheckConstraint("CK_reward_audits_item_reward_shape", "(item_id IS NULL AND item_quantity IS NULL) OR (item_id IS NOT NULL AND item_quantity IS NOT NULL AND item_id > 0 AND item_quantity > 0)");
                    table.ForeignKey(
                        name: "FK_reward_audits_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "player_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reward_audits_player_id",
                table: "reward_audits",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "IX_reward_audits_request_id",
                table: "reward_audits",
                column: "request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventories");

            migrationBuilder.DropTable(
                name: "player_wallets");

            migrationBuilder.DropTable(
                name: "reward_audits");
        }
    }
}
