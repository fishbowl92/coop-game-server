using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableMatchmakingOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "matchmaking_operations",
                columns: table => new
                {
                    operation_key = table.Column<string>(type: "character varying(133)", maxLength: 133, nullable: false),
                    request_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    leader_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_payload_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matchmaking_operations", x => x.operation_key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_matchmaking_operations_next_attempt_at_operation_key",
                table: "matchmaking_operations",
                columns: new[] { "next_attempt_at", "operation_key" },
                filter: "result_payload_json IS NULL");

            // 이전 취소 경로가 방만 완료하고 티켓 해제에 실패한 행을 기존 복구 루프에 다시 연결합니다.
            // 방·보상·최초 요청의 내용은 바꾸지 않고 후처리 대기 여부만 보정합니다.
            migrationBuilder.Sql("""
                UPDATE game_rooms AS room SET finalization_pending = TRUE
                WHERE room.lifecycle = 2 AND EXISTS (
                    SELECT 1 FROM match_queue_tickets AS ticket
                    WHERE ticket.room_id = room.room_id AND ticket.status = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "matchmaking_operations");
        }
    }
}
