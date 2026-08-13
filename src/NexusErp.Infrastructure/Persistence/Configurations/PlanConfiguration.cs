using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans");
        b.HasKey(x => x.Id);

        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Price).HasPrecision(18, 4).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Cycle).HasConversion<int>();
        b.Property(x => x.BillingModel).HasConversion<int>();
        b.Property(x => x.UsageUnitName).HasMaxLength(30);
        b.Property(x => x.IncludedUnits).HasPrecision(18, 4);
        b.Property(x => x.OveragePrice).HasPrecision(18, 4);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.HasOne(x => x.Product).WithMany()
         .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Code })
         .IsUnique()
         .HasFilter("is_deleted = false");
    }
}
