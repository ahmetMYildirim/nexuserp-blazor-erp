using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IptalSebebi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancellation_note",
                table: "subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cancellation_reason",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancellation_note",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                table: "subscriptions");
        }
    }
}
