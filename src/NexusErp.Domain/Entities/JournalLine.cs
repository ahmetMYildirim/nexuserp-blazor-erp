using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Muhasebe fişi satırı. Bir satır YA borç YA alacak tarafındadır; ikisi
/// birden dolu olamaz.
///
/// ⚠️ Bu kural hem burada hem veri tabanında CHECK constraint ile zorlanıyor.
/// Neden iki yerde: domain kuralı uygulama üzerinden geçmeyen bir yol
/// (migration, elle SQL, ileride eklenecek toplu içe aktarma) ile atlanabilir.
/// Muhasebede bozuk veri geri dönüşü olmayan bir hatadır.
/// </summary>
public sealed class JournalLine : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = default!;

    public int LineNumber { get; set; }

    public Guid AccountId { get; set; }
    public Account Account { get; set; } = default!;

    /// <summary>Hesap kodu snapshot'ı — hesap adı sonradan değişse de fiş sabit kalır.</summary>
    public string AccountCode { get; set; } = default!;
    public string AccountName { get; set; } = default!;

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    public string? Description { get; set; }

    /// <summary>Cari hesap bağlantısı — 120/320 hesaplarında hangi cariye ait.</summary>
    public Guid? PartyId { get; set; }

    public decimal SignedAmount => Debit - Credit;

    public void EnsureValid()
    {
        if (Debit < 0 || Credit < 0)
            throw new DomainException($"{LineNumber}. satırda negatif tutar olamaz.");

        if (Debit > 0 && Credit > 0)
            throw new DomainException(
                $"{LineNumber}. satırda hem borç hem alacak dolu. Bir satır tek taraflıdır.");

        if (Debit == 0 && Credit == 0)
            throw new DomainException($"{LineNumber}. satırda tutar girilmemiş.");
    }
}
