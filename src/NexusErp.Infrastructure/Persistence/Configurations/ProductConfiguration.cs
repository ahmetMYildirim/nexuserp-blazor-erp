using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products");
        b.HasKey(x => x.Id);

        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(300).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Kind).HasConversion<int>();
        b.Property(x => x.Unit).HasMaxLength(20).IsRequired();
        b.Property(x => x.UnitPrice).HasPrecision(18, 4).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.WithholdingRate).HasPrecision(9, 6);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        // Restrict: KDV oranı kullanımdaysa silinemez. Cascade olsaydı bir oranı silmek
        // tüm ürünleri silerdi — ERP'de neredeyse hiçbir zaman cascade istemezsin.
        b.HasOne(x => x.TaxRate)
         .WithMany()
         .HasForeignKey(x => x.TaxRateId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Code })
         .IsUnique()
         .HasFilter("is_deleted = false");

        b.HasIndex(x => new { x.TenantId, x.Name });
    }
}
