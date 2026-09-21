using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Betsi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredObservationTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BreathingDetails",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BreathingFinding",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CirculationDetails",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CirculationFinding",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MobilityDetails",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MobilityFinding",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SbarAssessment",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SbarBackground",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SbarRecommendation",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SbarSituation",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BreathingDetails",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "BreathingFinding",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "CirculationDetails",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "CirculationFinding",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "MobilityDetails",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "MobilityFinding",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "SbarAssessment",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "SbarBackground",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "SbarRecommendation",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "SbarSituation",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "dbo",
                table: "ClinicalObservations");
        }
    }
}
