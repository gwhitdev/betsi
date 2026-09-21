using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Betsi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPainAssessmentDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PainCharacter",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PainLocation",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PainOnsetAt",
                schema: "dbo",
                table: "ClinicalObservations",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PainCharacter",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "PainLocation",
                schema: "dbo",
                table: "ClinicalObservations");

            migrationBuilder.DropColumn(
                name: "PainOnsetAt",
                schema: "dbo",
                table: "ClinicalObservations");
        }
    }
}
