using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> b)
    {
        b.ToTable("usage_records");
        b.HasKey(x => x.Id);

        b.Property(x => x.Quantity).HasPrecision(18, 4);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.ExternalId).HasMaxLength(100);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        // Abonelik silinse bile kullanım geçmişi durur → Restrict.
        b.HasOne(x => x.Subscription).WithMany()
         .HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);

        // ⚠️ Entegrasyon tekrar denerse ikinci kayıt buraya takılır. Kullanımın iki
        // kez sayılması müşteriye fazladan fatura çıkarır — parasal sonucu var.
        b.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.ExternalId })
         .IsUnique()
         .HasFilter("external_id IS NOT NULL AND is_deleted = false");

        // Faturalandırmanın asıl sorgusu: "bu aboneliğin faturalanmamış kayıtları".
        // Kısmi index — faturalanmış kayıtlar zamanla çoğunluğu oluşturacak ama
        // hiçbir zaman bu sorguya girmeyecek.
        b.HasIndex(x => new { x.SubscriptionId, x.OccurredOn })
         .HasFilter("invoice_id IS NULL AND is_deleted = false");

        b.HasIndex(x => new { x.TenantId, x.OccurredOn });
    }
}
