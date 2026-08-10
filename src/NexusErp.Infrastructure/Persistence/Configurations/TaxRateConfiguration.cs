using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> b)
    {
        b.ToTable("tax_rates");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Rate).HasPrecision(9, 6).IsRequired();   // %20 → 0,200000
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.HasIndex(x => new { x.TenantId, x.Code })
         .IsUnique()
         .HasFilter("is_deleted = false");
    }
}
