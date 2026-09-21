using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Betsi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicalObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CarerName",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CarerPresenceRecordedAt",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CarerPresent",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CarerRelationship",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeteriorationFlagged",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeteriorationFlaggedAt",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SafeguardingConcernRaised",
                schema: "dbo",
                table: "PatientEpisodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ClinicalObservations",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientEpisodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    RespiratoryRate = table.Column<int>(type: "int", nullable: true),
                    OxygenSaturation = table.Column<int>(type: "int", nullable: true),
                    OnSupplementalOxygen = table.Column<bool>(type: "bit", nullable: false),
                    SystolicBloodPressure = table.Column<int>(type: "int", nullable: true),
                    Pulse = table.Column<int>(type: "int", nullable: true),
                    Consciousness = table.Column<int>(type: "int", nullable: true),
                    Temperature = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    PainScore = table.Column<int>(type: "int", nullable: true),
                    PainScale = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AgeBand = table.Column<int>(type: "int", nullable: false),
                    EarlyWarningScore = table.Column<int>(type: "int", nullable: true),
                    HighestSingleParameter = table.Column<int>(type: "int", nullable: true),
                    ScoreUnavailable = table.Column<int>(type: "int", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedByRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SupersedesObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersededByObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicalObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClinicalObservations_ClinicalObservations_SupersedesObservationId",
                        column: x => x.SupersedesObservationId,
                        principalSchema: "dbo",
                        principalTable: "ClinicalObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicalObservations_PatientEpisodes_PatientEpisodeId",
                        column: x => x.PatientEpisodeId,
                        principalSchema: "dbo",
                        principalTable: "PatientEpisodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalObservations_PatientEpisodeId_RecordedAt_Id",
                schema: "dbo",
                table: "ClinicalObservations",
                columns: new[] { "PatientEpisodeId", "RecordedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalObservations_SupersedesObservationId",
                schema: "dbo",
                table: "ClinicalObservations",
                column: "SupersedesObservationId",
                unique: true,
                filter: "[SupersedesObservationId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicalObservations",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "CarerName",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "CarerPresenceRecordedAt",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "CarerPresent",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "CarerRelationship",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "DeteriorationFlagged",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "DeteriorationFlaggedAt",
                schema: "dbo",
                table: "PatientEpisodes");

            migrationBuilder.DropColumn(
                name: "SafeguardingConcernRaised",
                schema: "dbo",
                table: "PatientEpisodes");
        }
    }
}
