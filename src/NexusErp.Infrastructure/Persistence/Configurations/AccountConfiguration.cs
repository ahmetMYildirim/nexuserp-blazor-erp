using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts");
        b.HasKey(x => x.Id);

        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Type).HasConversion<int>();
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        // Üst hesap silinirse alt hesaplar öksüz kalmasın → Restrict.
        b.HasOne(x => x.Parent).WithMany()
         .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);

        // Hesap kodu tenant içinde benzersiz. Aynı kodun iki kez açılması
        // mizanda hesabı ikiye böler ve toplamlar doğru görünse de hiçbir
        // hesabın bakiyesi doğru olmaz.
        b.HasIndex(x => new { x.TenantId, x.Code })
         .IsUnique()
         .HasFilter("is_deleted = false");

        b.HasIndex(x => new { x.TenantId, x.Type });
        b.HasIndex(x => new { x.TenantId, x.ParentId });
    }
}
