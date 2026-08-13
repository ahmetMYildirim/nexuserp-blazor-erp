using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KullanimBazliFaturalama : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "billing_model",
                table: "plans",
                type: "integer",
                nullable: false,
                // ⚠️ 1 = BillingModel.Flat. Varsayılan 0 BIRAKILAMAZ: enum'da 0'a
                // karşılık gelen üye yok, mevcut planlar ne Flat ne Metered sayılır
                // ve faturalandırma onlara HİÇ satır üretmez — abonelikler sessizce
                // faturalanmayı bırakır. Şema değişikliğinin en sinsi hata türü.
                defaultValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "included_units",
                table: "plans",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "overage_price",
                table: "plans",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "usage_unit_name",
                table: "plans",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // Mevcut satırların geri doldurulması. defaultValue yalnızca YENİ satırlara
            // uygulanır; kolon eklenirken var olan satırlar 0 ile dolar.
            migrationBuilder.Sql("UPDATE plans SET billing_model = 1 WHERE billing_model = 0;");

            migrationBuilder.CreateTable(
                name: "usage_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_usage_records_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_subscription_id_occurred_on",
                table: "usage_records",
                columns: new[] { "subscription_id", "occurred_on" },
                filter: "invoice_id IS NULL AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_tenant_id_occurred_on",
                table: "usage_records",
                columns: new[] { "tenant_id", "occurred_on" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_tenant_id_subscription_id_external_id",
                table: "usage_records",
                columns: new[] { "tenant_id", "subscription_id", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_records");

            migrationBuilder.DropColumn(
                name: "billing_model",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "included_units",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "overage_price",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "usage_unit_name",
                table: "plans");
        }
    }
}
