using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Betsi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AffectedAggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AffectedAggregateType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Context = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DomainEvents",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventData = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Escalations",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientEpisodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QueueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    ResponsibleRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AcknowledgedByActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcknowledgementDueAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Escalations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    CurrentOccupancy = table.Column<int>(type: "int", nullable: false),
                    QueueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EscalationEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventData = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PatientEpisodes",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    NhsNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DateOfBirth = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QueuePosition = table.Column<int>(type: "int", nullable: true),
                    ArrivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TriageStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TreatmentStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientEpisodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Queues",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HasExceededEscalationThreshold = table.Column<bool>(type: "bit", nullable: false),
                    ExceededThresholdAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PatientIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Queues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_Action",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_ActorId",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "TenantId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_AffectedAggregateId",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "TenantId", "AffectedAggregateId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_CreatedAt",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_Outcome",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_TenantId_AggregateId_Version",
                schema: "dbo",
                table: "DomainEvents",
                columns: new[] { "TenantId", "AggregateId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_TenantId_AggregateType",
                schema: "dbo",
                table: "DomainEvents",
                columns: new[] { "TenantId", "AggregateType" });

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_TenantId_EventId",
                schema: "dbo",
                table: "DomainEvents",
                columns: new[] { "TenantId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_TenantId_EventType",
                schema: "dbo",
                table: "DomainEvents",
                columns: new[] { "TenantId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_DomainEvents_TenantId_OccurredAt",
                schema: "dbo",
                table: "DomainEvents",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Escalations_TenantId_AcknowledgementDueAt",
                schema: "dbo",
                table: "Escalations",
                columns: new[] { "TenantId", "AcknowledgementDueAt" },
                filter: "[AcknowledgementDueAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Escalations_TenantId_CreatedAt",
                schema: "dbo",
                table: "Escalations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Escalations_TenantId_LocationId",
                schema: "dbo",
                table: "Escalations",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Escalations_TenantId_PatientEpisodeId",
                schema: "dbo",
                table: "Escalations",
                columns: new[] { "TenantId", "PatientEpisodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_Escalations_TenantId_State",
                schema: "dbo",
                table: "Escalations",
                columns: new[] { "TenantId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_Name",
                schema: "dbo",
                table: "Locations",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_State",
                schema: "dbo",
                table: "Locations",
                columns: new[] { "TenantId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId_EventId",
                schema: "dbo",
                table: "OutboxMessages",
                columns: new[] { "TenantId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantId_ProcessedAt",
                schema: "dbo",
                table: "OutboxMessages",
                columns: new[] { "TenantId", "ProcessedAt" },
                filter: "[ProcessedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientEpisodes_TenantId_ArrivedAt",
                schema: "dbo",
                table: "PatientEpisodes",
                columns: new[] { "TenantId", "ArrivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientEpisodes_TenantId_LocationId",
                schema: "dbo",
                table: "PatientEpisodes",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientEpisodes_TenantId_NhsNumber",
                schema: "dbo",
                table: "PatientEpisodes",
                columns: new[] { "TenantId", "NhsNumber" },
                unique: true,
                filter: "[NhsNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientEpisodes_TenantId_State",
                schema: "dbo",
                table: "PatientEpisodes",
                columns: new[] { "TenantId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Queues_TenantId_HasExceededEscalationThreshold",
                schema: "dbo",
                table: "Queues",
                columns: new[] { "TenantId", "HasExceededEscalationThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_Queues_TenantId_LocationId",
                schema: "dbo",
                table: "Queues",
                columns: new[] { "TenantId", "LocationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "DomainEvents",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Escalations",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Locations",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "PatientEpisodes",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Queues",
                schema: "dbo");
        }
    }
}
