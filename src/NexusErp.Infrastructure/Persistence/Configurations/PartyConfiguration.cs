using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class PartyConfiguration : IEntityTypeConfiguration<Party>
{
    public void Configure(EntityTypeBuilder<Party> b)
    {
        b.ToTable("parties");
        b.HasKey(x => x.Id);

        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        b.Property(x => x.Type).HasConversion<int>();
        b.Property(x => x.TaxNumber).HasMaxLength(11);
        b.Property(x => x.TaxNumberKind).HasConversion<int?>();
        b.Property(x => x.TaxOffice).HasMaxLength(100);
        b.Property(x => x.ContactName).HasMaxLength(150);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.District).HasMaxLength(80);
        b.Property(x => x.City).HasMaxLength(80);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.CreditLimit).HasPrecision(18, 4);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        // ⚠️ PARTIAL unique index. Bu filtre olmadan silinmiş "MUS0001" kodu index'i
        // işgal eder ve aynı kodla yeni cari açılamaz.
        // Ham SQL yazıyoruz → kolon adı snake_case olmak ZORUNDA.
        b.HasIndex(x => new { x.TenantId, x.Code })
         .IsUnique()
         .HasFilter("is_deleted = false");

        b.HasIndex(x => new { x.TenantId, x.Title });
        b.HasIndex(x => new { x.TenantId, x.TaxNumber });
    }
}
