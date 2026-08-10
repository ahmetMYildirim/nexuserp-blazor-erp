using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class InvoiceCounterConfiguration : IEntityTypeConfiguration<InvoiceCounter>
{
    public void Configure(EntityTypeBuilder<InvoiceCounter> b)
    {
        b.ToTable("invoice_counters");
        b.HasKey(x => x.Id);

        b.Property(x => x.Series).HasMaxLength(3).IsRequired();
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        // ⚠️ Bu unique index ŞART: INSERT ... ON CONFLICT (tenant_id, series, year)
        // buna dayanıyor. Olmadan numaralandırma çalışmaz.
        // Filtre YOK — sayaç satırı soft delete edilmez.
        b.HasIndex(x => new { x.TenantId, x.Series, x.Year }).IsUnique();
    }
}
