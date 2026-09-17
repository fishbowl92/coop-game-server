using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAuditPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audits",
                columns: table => new
                {
                    admin_audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    administrator_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    gold_amount = table.Column<long>(type: "bigint", nullable: false),
                    item_id = table.Column<int>(type: "integer", nullable: true),
                    item_quantity = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    result = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audits", x => x.admin_audit_id);
                    table.CheckConstraint("CK_admin_audits_action", "action = 1");
                    table.CheckConstraint("CK_admin_audits_gold_nonnegative", "gold_amount >= 0");
                    table.CheckConstraint("CK_admin_audits_has_reward", "gold_amount > 0 OR item_id IS NOT NULL");
                    table.CheckConstraint("CK_admin_audits_item_reward_shape", "(item_id IS NULL AND item_quantity IS NULL) OR (item_id IS NOT NULL AND item_quantity IS NOT NULL AND item_id > 0 AND item_quantity > 0)");
                    table.CheckConstraint("CK_admin_audits_result", "result = 1");
                    table.ForeignKey(
                        name: "FK_admin_audits_accounts_administrator_account_id",
                        column: x => x.administrator_account_id,
                        principalTable: "accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_admin_audits_players_target_player_id",
                        column: x => x.target_player_id,
                        principalTable: "players",
                        principalColumn: "player_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audits_administrator_account_id",
                table: "admin_audits",
                column: "administrator_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audits_request_id",
                table: "admin_audits",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_audits_target_player_id_created_at",
                table: "admin_audits",
                columns: new[] { "target_player_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audits");
        }
    }
}
