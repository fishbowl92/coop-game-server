using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteMatchQueueTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_match_queue_tickets_status",
                table: "match_queue_tickets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_match_queue_tickets_status",
                table: "match_queue_tickets",
                sql: "status IN (0, 1, 2, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_match_queue_tickets_status",
                table: "match_queue_tickets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_match_queue_tickets_status",
                table: "match_queue_tickets",
                sql: "status IN (0, 1, 2)");
        }
    }
}
