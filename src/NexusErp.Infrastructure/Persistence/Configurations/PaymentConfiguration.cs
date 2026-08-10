using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments");
        b.HasKey(x => x.Id);

        b.Property(x => x.Number).HasMaxLength(20);
        b.Property(x => x.Method).HasConversion<int>();
        b.Property(x => x.Amount).HasPrecision(18, 4).IsRequired();
        b.Property(x => x.AllocatedAmount).HasPrecision(18, 4);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.HasOne(x => x.Party).WithMany()
         .HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Allocations).WithOne(x => x.Payment)
         .HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.TenantId, x.Number })
         .IsUnique()
         .HasFilter("number IS NOT NULL AND is_deleted = false");

        b.HasIndex(x => new { x.TenantId, x.PartyId, x.PaymentDate });
    }
}

public sealed class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> b)
    {
        b.ToTable("payment_allocations");
        b.HasKey(x => x.Id);

        b.Property(x => x.Amount).HasPrecision(18, 4).IsRequired();
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.HasOne(x => x.Invoice).WithMany()
         .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.InvoiceId);
    }
}

public sealed class PartyLedgerEntryConfiguration : IEntityTypeConfiguration<PartyLedgerEntry>
{
    public void Configure(EntityTypeBuilder<PartyLedgerEntry> b)
    {
        b.ToTable("party_ledger_entries");
        b.HasKey(x => x.Id);

        b.Property(x => x.Type).HasConversion<int>();
        b.Property(x => x.Debit).HasPrecision(18, 4);
        b.Property(x => x.Credit).HasPrecision(18, 4);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.Property(x => x.DocumentNumber).HasMaxLength(20);
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        b.HasOne(x => x.Party).WithMany()
         .HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);

        // Cari ekstre ve bakiye sorgusunun kapsayıcı index'i
        b.HasIndex(x => new { x.TenantId, x.PartyId, x.EntryDate });
    }
}
