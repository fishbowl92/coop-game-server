using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoopGameServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyMatchLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "player_id",
                table: "party_requests",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "result_current_room_id",
                table: "party_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "room_id",
                table: "party_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_room_id",
                table: "parties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_party_requests_command_subject",
                table: "party_requests",
                sql: "(command_kind IN ('StartGame', 'CompleteGame') AND player_id IS NULL AND room_id IS NOT NULL) OR (command_kind IN ('Create', 'Join', 'Leave', 'Disband', 'QueueForMatch', 'CancelMatchQueue') AND player_id IS NOT NULL AND room_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_parties_lifecycle",
                table: "parties",
                sql: "lifecycle IN (0, 1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_parties_state_shape",
                table: "parties",
                sql: "(lifecycle = 1 AND leader_player_id IS NULL AND current_room_id IS NULL) OR (lifecycle IN (0, 2) AND leader_player_id IS NOT NULL AND current_room_id IS NULL) OR (lifecycle = 3 AND leader_player_id IS NOT NULL AND current_room_id IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_party_requests_command_subject",
                table: "party_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_parties_lifecycle",
                table: "parties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_parties_state_shape",
                table: "parties");

            migrationBuilder.DropColumn(
                name: "result_current_room_id",
                table: "party_requests");

            migrationBuilder.DropColumn(
                name: "room_id",
                table: "party_requests");

            migrationBuilder.DropColumn(
                name: "current_room_id",
                table: "parties");

            migrationBuilder.AlterColumn<Guid>(
                name: "player_id",
                table: "party_requests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
