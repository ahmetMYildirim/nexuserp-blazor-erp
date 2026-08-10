using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ToTable("invoice_lines");
        b.HasKey(x => x.Id);

        b.Property(x => x.ProductCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.ProductName).HasMaxLength(300).IsRequired();
        b.Property(x => x.Unit).HasMaxLength(20).IsRequired();
        b.Property(x => x.DiscountType).HasConversion<int>();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.Property(x => x.Quantity).HasPrecision(18, 6);      // 1,5 kg · 0,25 saat

        foreach (var money in new[]
        {
            nameof(InvoiceLine.UnitPrice), nameof(InvoiceLine.DiscountValue),
            nameof(InvoiceLine.GrossAmount), nameof(InvoiceLine.DiscountAmount),
            nameof(InvoiceLine.DocumentDiscountShare), nameof(InvoiceLine.TaxBase),
            nameof(InvoiceLine.TaxAmount), nameof(InvoiceLine.WithholdingAmount),
            nameof(InvoiceLine.LineTotal)
        })
            b.Property(money).HasPrecision(18, 4);

        b.Property(x => x.TaxRate).HasPrecision(9, 6);
        b.Property(x => x.WithholdingRate).HasPrecision(9, 6);

        b.HasIndex(x => new { x.InvoiceId, x.LineNumber });
        b.HasIndex(x => x.ProductId);
    }
}
