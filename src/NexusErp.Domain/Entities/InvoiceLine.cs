using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

public sealed class InvoiceLine : AuditableEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = default!;

    public int LineNumber { get; set; }

    // --- Ürün SNAPSHOT'ı ---
    // Ürün adı/fiyatı sonradan değişse bile fatura değişmemeli: muhasebe belgesi
    // değişmezdir. Bilinçli denormalizasyon (Bölüm 07).
    public Guid? ProductId { get; set; }
    public string ProductCode { get; set; } = default!;
    public string ProductName { get; set; } = default!;
    public string Unit { get; set; } = "Adet";

    // --- Girdi ---
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public DiscountType DiscountType { get; set; } = DiscountType.None;
    public decimal DiscountValue { get; set; }

    public Guid? TaxRateId { get; set; }
    public decimal TaxRate { get; set; }              // snapshot, örn. 0,20
    public decimal? WithholdingRate { get; set; }     // snapshot, örn. 0,70

    // --- Hesaplanan (saklanır: rapor SUM'ı, kural değişse de geçmişin sabitliği,
    //     e-Fatura XML'i satır bazında bunları ister) ---
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal DocumentDiscountShare { get; set; }
    public decimal TaxBase { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal WithholdingAmount { get; set; }
    public decimal LineTotal { get; set; }

    public string? Description { get; set; }
}
