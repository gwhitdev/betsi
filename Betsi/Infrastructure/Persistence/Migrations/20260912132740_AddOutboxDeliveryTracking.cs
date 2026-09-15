using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Betsi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxDeliveryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                schema: "dbo",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "dbo",
                table: "OutboxMessages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempts",
                schema: "dbo",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "dbo",
                table: "OutboxMessages");
        }
    }
}
