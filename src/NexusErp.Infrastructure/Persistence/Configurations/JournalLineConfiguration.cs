using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> b)
    {
        b.ToTable("journal_lines", t =>
        {
            // ⚠️ Bir satır YA borç YA alacak tarafındadır. İkisi birden dolu
            // bir satır çift taraflı kaydın anlamını bozar: aynı tutar aynı
            // hesabın iki tarafında birden görünür, bakiye sıfır çıkar ve
            // hareket görünmez olur.
            t.HasCheckConstraint(
                "ck_journal_lines_single_side",
                "debit >= 0 AND credit >= 0 AND NOT (debit > 0 AND credit > 0) " +
                "AND (debit > 0 OR credit > 0)");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.AccountCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.AccountName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.Property(x => x.Debit).HasPrecision(18, 4);
        b.Property(x => x.Credit).HasPrecision(18, 4);

        // Hesap fişten bağımsız yaşar; hareket görmüş hesap silinemez → Restrict.
        b.HasOne(x => x.Account).WithMany()
         .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);

        // Mizan: hesap bazında borç/alacak toplamı.
        b.HasIndex(x => new { x.TenantId, x.AccountId });
        b.HasIndex(x => x.JournalEntryId);
    }
}
