using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions");
        b.HasKey(x => x.Id);

        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.CustomPrice).HasPrecision(18, 4);
        b.Property(x => x.Quantity).HasPrecision(18, 6);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.HasOne(x => x.Party).WithMany()
         .HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Plan).WithMany()
         .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);

        // Faturalandırma işinin her turda taradığı sorgu
        b.HasIndex(x => new { x.TenantId, x.Status, x.NextBillingDate });
        b.HasIndex(x => new { x.TenantId, x.PartyId });
    }
}
