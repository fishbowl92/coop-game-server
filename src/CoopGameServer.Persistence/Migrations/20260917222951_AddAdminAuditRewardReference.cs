using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAuditRewardReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_reward_audits_request_id",
                table: "reward_audits",
                column: "request_id");

            migrationBuilder.AddForeignKey(
                name: "FK_admin_audits_reward_audits_request_id",
                table: "admin_audits",
                column: "request_id",
                principalTable: "reward_audits",
                principalColumn: "request_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_admin_audits_reward_audits_request_id",
                table: "admin_audits");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_reward_audits_request_id",
                table: "reward_audits");
        }
    }
}
