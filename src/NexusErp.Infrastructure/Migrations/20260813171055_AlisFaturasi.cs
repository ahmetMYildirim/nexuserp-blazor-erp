using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlisFaturasi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_tenant_id_number",
                table: "invoices");

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "invoices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supplier_invoice_no",
                table: "invoices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_number",
                table: "invoices",
                columns: new[] { "tenant_id", "number" },
                unique: true,
                filter: "number IS NOT NULL AND is_deleted = false AND type <> 4");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_party_id_supplier_invoice_no",
                table: "invoices",
                columns: new[] { "tenant_id", "party_id", "supplier_invoice_no" },
                unique: true,
                filter: "supplier_invoice_no IS NOT NULL AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_tenant_id_number",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoices_tenant_id_party_id_supplier_invoice_no",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "supplier_invoice_no",
                table: "invoices");

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "invoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_number",
                table: "invoices",
                columns: new[] { "tenant_id", "number" },
                unique: true,
                filter: "number IS NOT NULL AND is_deleted = false");
        }
    }
}
