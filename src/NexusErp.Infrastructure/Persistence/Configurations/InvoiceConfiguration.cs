using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("invoices");
        b.HasKey(x => x.Id);

        b.Property(x => x.Number).HasMaxLength(50);
        b.Property(x => x.SupplierInvoiceNo).HasMaxLength(50);
        b.Property(x => x.Series).HasMaxLength(3).IsRequired();
        b.Property(x => x.Type).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.DocumentDiscountType).HasConversion<int>();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.PartyTitle).HasMaxLength(300).IsRequired();
        b.Property(x => x.PartyTaxNumber).HasMaxLength(11);
        b.Property(x => x.PartyTaxOffice).HasMaxLength(100);
        b.Property(x => x.PartyAddress).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        foreach (var money in new[]
        {
            nameof(Invoice.GrossTotal), nameof(Invoice.DiscountTotal),
            nameof(Invoice.TaxBaseTotal), nameof(Invoice.TaxTotal),
            nameof(Invoice.WithholdingTotal), nameof(Invoice.GrandTotal),
            nameof(Invoice.PaidAmount), nameof(Invoice.DocumentDiscountValue)
        })
            b.Property(money).HasPrecision(18, 4);

        b.Property(x => x.ExchangeRate).HasPrecision(18, 6);

        // Cari faturadan bağımsız yaşar → Restrict
        b.HasOne(x => x.Party).WithMany()
         .HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);

        // Satır faturasız var olamaz (composition) → Cascade
        b.HasMany(x => x.Lines).WithOne(x => x.Invoice)
         .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);

        // PostgreSQL'in gizli xmin sistem kolonu = iyimser kilitleme (optimistic locking).
        // Her UPDATE'te otomatik değişen transaction kimliği; ek kolon ve ek migration YOK.
        // İki kullanıcı aynı faturaya tahsilat işlerse ikincisi
        // DbUpdateConcurrencyException alır (Bölüm 10).
        //
        // ⚠️ Eski API UseXminAsConcurrencyToken() Npgsql 7'de obsolete edildi, sonra
        // kaldırıldı. Güncel karşılığı gölge (shadow) property tanımıdır:
        b.Property<uint>("xmin")
         .HasColumnName("xmin")
         .HasColumnType("xid")
         .ValueGeneratedOnAddOrUpdate()
         .IsConcurrencyToken();

        // Fatura numarası tenant içinde benzersiz — son savunma hattı.
        // ⚠️ type <> 4 (Alış) HARİÇ: alış faturasının numarası TEDARİKÇİNİN numarasıdır.
        // İki farklı tedarikçi pekâlâ aynı numarayı kullanabilir; bu index'in kapsamına
        // alsaydık ikinci tedarikçinin faturası kaydedilemezdi.
        b.HasIndex(x => new { x.TenantId, x.Number })
         .IsUnique()
         .HasFilter("number IS NOT NULL AND is_deleted = false AND type <> 4");

        // ⚠️ Alış tarafının idempotency garantisi: AYNI tedarikçiden AYNI numara
        // iki kez girilemez. Mükerrer alış faturası hem cariyi hem gideri şişirir;
        // el ile veri girişinde en sık yapılan hata budur.
        b.HasIndex(x => new { x.TenantId, x.PartyId, x.SupplierInvoiceNo })
         .IsUnique()
         .HasFilter("supplier_invoice_no IS NOT NULL AND is_deleted = false");

        // ⚠️ IDEMPOTENCY (Bölüm 09): aynı abonelik + aynı dönem için İKİNCİ fatura
        // üretilemez. Garanti iş mantığında değil, VERİ TABANINDA.
        b.HasIndex(x => new { x.SubscriptionId, x.PeriodStart })
         .IsUnique()
         .HasFilter("subscription_id IS NOT NULL AND is_deleted = false");

        // Liste ve rapor sorguları
        b.HasIndex(x => new { x.TenantId, x.IssueDate });
        b.HasIndex(x => new { x.TenantId, x.PartyId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.Status, x.DueDate });   // yaşlandırma raporu
    }
}
