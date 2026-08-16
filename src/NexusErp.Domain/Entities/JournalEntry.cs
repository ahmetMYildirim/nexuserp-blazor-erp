using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Muhasebe fişi — çift taraflı kaydın belgesi.
///
/// Fişin tek değişmezi (invariant) şudur: SUM(Borç) = SUM(Alacak).
/// Bu eşitlik bozulursa mizan tutmaz, bilanço denkleşmez ve hatanın hangi
/// fişten geldiğini bulmak binlerce kayıt arasında elle arama demektir.
/// Bu yüzden dengesiz fiş KAPATILAMAZ ve kapatılmamış fiş rapora girmez.
///
/// Taslak / kesinleşmiş ayrımı fatura ile aynı mantıkta: taslak serbestçe
/// değiştirilebilir, kesinleşmiş fiş değiştirilemez — düzeltme ancak ters
/// kayıtla yapılır (ADR-009: muhasebe verisi silinmez).
/// </summary>
public sealed class JournalEntry : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    /// <summary>Fiş numarası — kendi serisi (MUH), fatura serisinden bağımsız.</summary>
    public string? Number { get; set; }
    public int Year { get; set; }

    public DateOnly EntryDate { get; set; }
    public string Description { get; set; } = default!;

    public bool IsPosted { get; set; }
    public DateTimeOffset? PostedAt { get; set; }

    /// <summary>
    /// Fişin kaynağı ve kaynak belgenin kimliği.
    /// (TenantId, SourceType, SourceId) unique index'i aynı belgeden ikinci
    /// fiş üretilmesini VERİ TABANI seviyesinde engelliyor — servis mantığı
    /// yanlışlıkla iki kez çağrılsa bile ikincisi INSERT'te patlar.
    /// </summary>
    public JournalSourceType SourceType { get; set; } = JournalSourceType.Manual;
    public Guid? SourceId { get; set; }
    public string? SourceDocumentNumber { get; set; }

    /// <summary>
    /// Satır toplamları fiş üzerinde saklanıyor. İki nedeni var:
    /// (1) mizan/bilanço sorgusu her seferinde satırları toplamak zorunda kalmaz,
    /// (2) veri tabanındaki CHECK constraint satırlar arasında toplam alamaz —
    ///     kolona yazılmış toplam üzerinden "kesinleşmiş fiş dengeli olmak
    ///     zorundadır" kuralı zorlanabilir hale gelir.
    /// </summary>
    public decimal DebitTotal { get; set; }
    public decimal CreditTotal { get; set; }

    public List<JournalLine> Lines { get; set; } = [];

    public bool IsEditable => !IsPosted;
    public bool IsBalanced => DebitTotal == CreditTotal;
    public decimal Difference => DebitTotal - CreditTotal;

    public string StatusText => IsPosted ? "Kesinleşti" : "Taslak";

    public string SourceText => SourceType switch
    {
        JournalSourceType.Manual => "Manuel",
        JournalSourceType.SalesInvoice => "Satış Faturası",
        JournalSourceType.PurchaseInvoice => "Alış Faturası",
        JournalSourceType.Payment => "Tahsilat",
        JournalSourceType.PaymentReversal => "Tahsilat İptali",
        _ => "?"
    };

    public void EnsureEditable()
    {
        if (!IsEditable)
            throw new DomainException(
                $"{Number ?? "Taslak"} fişi kesinleşmiş, değiştirilemez. " +
                "Düzeltme için ters kayıt fişi girin.");
    }

    /// <summary>Satır toplamlarını satırlardan YENİDEN hesaplar.</summary>
    public void RecalculateTotals()
    {
        DebitTotal = Lines.Sum(l => l.Debit);
        CreditTotal = Lines.Sum(l => l.Credit);
    }

    /// <summary>
    /// Fişi kesinleştirir. Bu noktadan sonra fiş rapora girer ve değiştirilemez.
    /// Dengesiz fiş buradan geçemez.
    /// </summary>
    public void Post(string number, DateTimeOffset now)
    {
        if (IsPosted)
            throw new DomainException("Fiş zaten kesinleşmiş.");

        if (Lines.Count < 2)
            throw new DomainException(
                "Muhasebe fişi en az iki satır içermelidir: her kaydın bir borç " +
                "bir alacak tarafı vardır.");

        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].LineNumber = i + 1;
            Lines[i].EnsureValid();
        }

        RecalculateTotals();

        if (!IsBalanced)
            throw new DomainException(
                $"Fiş dengesiz: borç {DebitTotal:N2} — alacak {CreditTotal:N2}, " +
                $"fark {Math.Abs(Difference):N2}. Dengelenmeden kesinleştirilemez.");

        if (DebitTotal == 0)
            throw new DomainException("Tutarı sıfır olan fiş kesinleştirilemez.");

        Number = number;
        IsPosted = true;
        PostedAt = now;
    }
}
