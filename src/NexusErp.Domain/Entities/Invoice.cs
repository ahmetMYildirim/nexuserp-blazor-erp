using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

public sealed class Invoice : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    // --- Belge kimliği ---
    /// <summary>GİB formatı: NEX2026000000001 (3 harf seri + 4 hane yıl + 9 hane sıra)</summary>
    public string? Number { get; set; }
    public string Series { get; set; } = default!;
    public int Year { get; set; }
    public long Sequence { get; set; }

    /// <summary>Elektronik Türk Tekil Numara — e-Fatura için benzersiz belge kimliği.</summary>
    public Guid Ettn { get; set; } = Guid.NewGuid();

    public InvoiceType Type { get; set; } = InvoiceType.Sales;
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    // --- Cari (fatura kesildiği andaki bilgiler: snapshot) ---
    public Guid PartyId { get; set; }
    public Party Party { get; set; } = default!;
    public string PartyTitle { get; set; } = default!;
    public string? PartyTaxNumber { get; set; }
    public string? PartyTaxOffice { get; set; }
    public string? PartyAddress { get; set; }

    // --- Tarihler ---
    // DateOnly: takvim günü, saat dilimi taşımaz. "3 Mart faturası" her yerde 3 Mart'tır.
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }

    // --- Para ---
    public string Currency { get; set; } = "TRY";
    public decimal ExchangeRate { get; set; } = 1m;

    // --- Belge geneli iskonto ---
    public DiscountType DocumentDiscountType { get; set; } = DiscountType.None;
    public decimal DocumentDiscountValue { get; set; }

    // --- Toplamlar (hesaplanır, saklanır) ---
    public decimal GrossTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxBaseTotal { get; set; }      // KDV matrahı
    public decimal TaxTotal { get; set; }
    public decimal WithholdingTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }        // Bölüm 10'da güncellenir

    public string? Notes { get; set; }

    // --- Abonelik bağlantısı (Bölüm 09) ---
    public Guid? SubscriptionId { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }

    public List<InvoiceLine> Lines { get; set; } = [];

    // ------------------------------------------------------------------
    // Durum makinesi — geçişler entity'nin kendi sorumluluğu
    // ------------------------------------------------------------------

    public decimal RemainingAmount => GrandTotal - PaidAmount;
    public bool IsEditable => Status == InvoiceStatus.Draft;

    /// <summary>Proforma cari bakiyeye işlemez — bağlayıcı olmayan tekliftir.</summary>
    public bool AffectsBalance =>
        Type != InvoiceType.Proforma &&
        Status is not (InvoiceStatus.Draft or InvoiceStatus.Cancelled);

    public void EnsureEditable()
    {
        if (!IsEditable)
            throw new DomainException(
                $"{Number ?? "Taslak"} faturası '{StatusText}' durumunda, değiştirilemez.");
    }

    public void MarkIssued(string number, long sequence, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Draft)
            throw new DomainException("Yalnızca taslak faturalar kesilebilir.");
        if (Lines.Count == 0)
            throw new DomainException("Satırı olmayan fatura kesilemez.");
        if (GrandTotal <= 0 && Type != InvoiceType.SalesReturn)
            throw new DomainException("Fatura tutarı sıfır veya negatif olamaz.");

        Number = number;
        Sequence = sequence;
        Status = InvoiceStatus.Issued;
        IssuedAt = now;
    }

    /// <summary>Kesilmiş fatura SİLİNMEZ — vergi mevzuatı gereği iptal edilir.</summary>
    public void Cancel()
    {
        if (Status is InvoiceStatus.Draft)
            throw new DomainException("Taslak fatura iptal edilmez, silinir.");
        if (Status is InvoiceStatus.Cancelled)
            throw new DomainException("Fatura zaten iptal edilmiş.");
        if (PaidAmount > 0)
            throw new DomainException(
                "Tahsilatı olan fatura iptal edilemez. Önce tahsilat kaydını geri alın " +
                "veya iade faturası kesin.");

        Status = InvoiceStatus.Cancelled;
    }

    /// <summary>Bölüm 10'da tahsilat sonrası çağrılır.</summary>
    public void RefreshPaymentStatus()
    {
        if (Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled) return;

        Status = PaidAmount switch
        {
            <= 0 => InvoiceStatus.Issued,
            _ when PaidAmount >= GrandTotal => InvoiceStatus.Paid,
            _ => InvoiceStatus.PartiallyPaid
        };
    }

    public string StatusText => Status switch
    {
        InvoiceStatus.Draft => "Taslak",
        InvoiceStatus.Issued => "Kesildi",
        InvoiceStatus.PartiallyPaid => "Kısmi Tahsil",
        InvoiceStatus.Paid => "Tahsil Edildi",
        InvoiceStatus.Cancelled => "İptal",
        _ => "?"
    };

    public string TypeText => Type switch
    {
        InvoiceType.Sales => "Satış",
        InvoiceType.SalesReturn => "İade",
        InvoiceType.Proforma => "Proforma",
        InvoiceType.Purchase => "Alış",
        _ => "?"
    };
}
