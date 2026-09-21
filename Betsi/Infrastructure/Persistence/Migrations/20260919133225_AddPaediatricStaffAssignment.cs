using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Betsi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaediatricStaffAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedStaffActorId",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedStaffName",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AssignedStaffPaediatricTrained",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedStaffRole",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PaediatricSkillGapAlertOpen",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StaffAssignedAt",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedStaffActorId",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "AssignedStaffName",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "AssignedStaffPaediatricTrained",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "AssignedStaffRole",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "PaediatricSkillGapAlertOpen",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "StaffAssignedAt",
                schema: "dbo",
                table: "PatientEpisodes");
        }
    }
}
