using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Betsi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEscalationEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "LocationId",
                schema: "dbo",
                table: "Escalations",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedByRole",
                schema: "dbo",
                table: "Escalations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AcknowledgementDeadlineMinutes",
                schema: "dbo",
                table: "Escalations",
                type: "int",
                nullable: false,
                // Hand edit: escalations created before this migration all had the fixed
                // 15-minute deadline. A default of 0 would give any of them that is later
                // reassigned an acknowledgement deadline of "now".
                defaultValue: 15);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                schema: "dbo",
                table: "Escalations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PolicyRevision",
                schema: "dbo",
                table: "Escalations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecommendedAction",
                schema: "dbo",
                table: "Escalations",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResolvedByActorId",
                schema: "dbo",
                table: "Escalations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedByRole",
                schema: "dbo",
                table: "Escalations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThresholdMinutes",
                schema: "dbo",
                table: "Escalations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TierLevel",
                schema: "dbo",
                table: "Escalations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitedMinutes",
                schema: "dbo",
                table: "Escalations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EscalationPolicies",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    FollowUpOwnerRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProposalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProposedByActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposedByRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProposedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RestoresRevision = table.Column<int>(type: "int", nullable: true),
                    DecidedByActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedByRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Tiers = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscalationPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FollowUpExceptions",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EscalationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientEpisodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EscalationResponsibleRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MissedDeadline = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OwnerRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ClosedByActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedByRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowUpExceptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Escalations_TenantId_PatientEpisodeId_TierLevel",
                schema: "dbo",
                table: "Escalations",
                columns: new[] { "TenantId", "PatientEpisodeId", "TierLevel" },
                unique: true,
                filter: "[TierLevel] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EscalationPolicies_TenantId_Revision",
                schema: "dbo",
                table: "EscalationPolicies",
                columns: new[] { "TenantId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EscalationPolicies_TenantId_State_EffectiveFrom",
                schema: "dbo",
                table: "EscalationPolicies",
                columns: new[] { "TenantId", "State", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpExceptions_TenantId_EscalationId_MissedDeadline",
                schema: "dbo",
                table: "FollowUpExceptions",
                columns: new[] { "TenantId", "EscalationId", "MissedDeadline" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpExceptions_TenantId_State",
                schema: "dbo",
                table: "FollowUpExceptions",
                columns: new[] { "TenantId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EscalationPolicies",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "FollowUpExceptions",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_Escalations_TenantId_PatientEpisodeId_TierLevel",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "AcknowledgedByRole",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "AcknowledgementDeadlineMinutes",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "PolicyRevision",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "RecommendedAction",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "ResolvedByActorId",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "ResolvedByRole",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "ThresholdMinutes",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "TierLevel",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.DropColumn(
                name: "WaitedMinutes",
                schema: "dbo",
                table: "Escalations");

            migrationBuilder.AlterColumn<Guid>(
                name: "LocationId",
                schema: "dbo",
                table: "Escalations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
