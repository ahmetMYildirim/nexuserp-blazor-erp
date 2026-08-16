using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        b.ToTable("journal_entries", t =>
        {
            // ⚠️ DENGELİ FİŞ GARANTİSİNİN VERİ TABANI AYAĞI.
            // Domain'deki JournalEntry.Post() zaten dengesiz fişi reddediyor ama
            // uygulamadan geçmeyen bir yol (elle SQL, migration, ileride eklenecek
            // toplu içe aktarma) o kontrolü atlar. Kesinleşmiş fiş dengesizse
            // mizan tutmaz ve hatanın kaynağını bulmak binlerce kaydı elle
            // taramak demektir — bu yüzden kural veri tabanında da duruyor.
            //
            // Taslak fiş dengesiz olabilir: kullanıcı satırları girerken ara
            // durumda zaten dengesizdir.
            t.HasCheckConstraint(
                "ck_journal_entries_posted_balanced",
                "NOT is_posted OR debit_total = credit_total");

            t.HasCheckConstraint(
                "ck_journal_entries_totals_non_negative",
                "debit_total >= 0 AND credit_total >= 0");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Number).HasMaxLength(50);
        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
        b.Property(x => x.SourceType).HasConversion<int>();
        b.Property(x => x.SourceDocumentNumber).HasMaxLength(50);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.Property(x => x.DebitTotal).HasPrecision(18, 4);
        b.Property(x => x.CreditTotal).HasPrecision(18, 4);

        b.HasMany(x => x.Lines).WithOne(x => x.JournalEntry)
         .HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Cascade);

        // Fiş numarası tenant içinde benzersiz.
        b.HasIndex(x => new { x.TenantId, x.Number })
         .IsUnique()
         .HasFilter("number IS NOT NULL AND is_deleted = false");

        // ⚠️ IDEMPOTENCY: aynı kaynak belgeden İKİNCİ fiş üretilemez.
        // Otomatik fiş üretimi yanlışlıkla iki kez çağrılırsa (retry, çift
        // tıklama, kod hatası) ikinci INSERT burada patlar. Çift fiş mizanı
        // bozmaz — dengeli kalır — ama ciroyu ve KDV'yi iki katı gösterir;
        // bu tür hata ancak beyanname aşamasında fark edilir.
        b.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceId })
         .IsUnique()
         .HasFilter("source_id IS NOT NULL AND is_deleted = false");

        // Mizan / bilanço / gelir tablosu sorguları tarih aralığı + kesinleşmiş
        // filtresiyle çalışıyor.
        b.HasIndex(x => new { x.TenantId, x.IsPosted, x.EntryDate });
    }
}
