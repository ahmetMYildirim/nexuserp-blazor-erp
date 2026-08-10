using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Cari hareket. Bakiye = SUM(Debit) − SUM(Credit).
///
/// Bakiyeyi kolonda tutmak yerine hareket tablosu tutuyoruz (Bölüm 10):
/// her kuruşun kaynağı belli, denetlenebilir ve yanlış kayıt ters kayıtla düzeltilebilir.
/// Kolonda tutulan bakiyede bir bug tüm geçmişi bozar ve geri dönüş olmaz.
///
/// Borç (Debit) artışı  = müşteri bize borçlandı (fatura kestik)
/// Alacak (Credit) artışı = müşteri ödedi
/// </summary>
public sealed class PartyLedgerEntry : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid PartyId { get; set; }
    public Party Party { get; set; } = default!;

    public DateOnly EntryDate { get; set; }
    public LedgerEntryType Type { get; set; }

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Currency { get; set; } = "TRY";

    public string Description { get; set; } = default!;

    // Kaynak belge
    public Guid? InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }
    public string? DocumentNumber { get; set; }

    public decimal SignedAmount => Debit - Credit;

    public string TypeText => Type switch
    {
        LedgerEntryType.Invoice => "Satış Faturası",
        LedgerEntryType.InvoiceReturn => "İade Faturası",
        LedgerEntryType.Payment => "Tahsilat",
        LedgerEntryType.Refund => "İade Ödemesi",
        LedgerEntryType.Adjustment => "Düzeltme",
        _ => "?"
    };
}
