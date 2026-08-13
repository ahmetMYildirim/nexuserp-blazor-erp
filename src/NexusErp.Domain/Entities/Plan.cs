using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

public sealed class Plan : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public string Code { get; set; } = default!;        // "PRO-AYLIK"
    public string Name { get; set; } = default!;        // "Pro Paket — Aylık"
    public string? Description { get; set; }

    /// <summary>KDV hariç dönem ücreti.</summary>
    public decimal Price { get; set; }
    public string Currency { get; set; } = "TRY";

    public BillingCycle Cycle { get; set; } = BillingCycle.Monthly;

    /// <summary>Ücretsiz deneme süresi (gün). 0 = deneme yok.</summary>
    public int TrialDays { get; set; }

    /// <summary>Faturada hangi ürün/hizmet olarak görünecek.</summary>
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = default!;

    public bool IsActive { get; set; } = true;

    // ------------------------------------------------------------------
    // Kullanım bazlı ücretlendirme
    // ------------------------------------------------------------------

    public BillingModel BillingModel { get; set; } = BillingModel.Flat;

    /// <summary>Faturada görünecek birim adı: "SMS", "GB", "API çağrısı".</summary>
    public string? UsageUnitName { get; set; }

    /// <summary>
    /// Taban ücrete DAHİL birim adedi. Abonelik miktarıyla çarpılır:
    /// 5 lisanslı, lisans başına 100 SMS dahil olan plan → 500 SMS.
    /// </summary>
    public decimal IncludedUnits { get; set; }

    /// <summary>Dahil birimleri aşan her birim için ücret (KDV hariç).</summary>
    public decimal OveragePrice { get; set; }

    public bool IsMetered => BillingModel is BillingModel.Metered or BillingModel.Hybrid;

    /// <summary>Sabit ücret satırı kesilir mi? Saf kullanım planında kesilmez.</summary>
    public bool HasFlatFee => BillingModel is BillingModel.Flat or BillingModel.Hybrid;

    /// <summary>
    /// Bu dönem için ücretsiz birim hakkı.
    /// ⚠️ Saf kullanım planında da olabilir (ücretsiz kota).
    /// </summary>
    public decimal AllowanceFor(decimal subscriptionQuantity) =>
        IncludedUnits * subscriptionQuantity;

    public string BillingModelText => BillingModel switch
    {
        BillingModel.Flat => "Sabit ücret",
        BillingModel.Metered => "Kullanım bazlı",
        BillingModel.Hybrid => "Sabit + kullanım",
        _ => "?"
    };

    /// <summary>
    /// MRR katkısı — dönem ne olursa olsun aya normalize edilir.
    /// 12.000 TL/yıl bir plan MRR'a 1.000 TL katar. Yanlış hesaplarsan rakamlar 12 kat şişer.
    /// </summary>
    /// ⚠️ Saf kullanım planı MRR'a KATILMAZ: taahhüt edilmiş yinelenen gelir yok,
    /// tutar her ay kullanımla değişir. MRR'ı "tahmin edilebilir gelir" olarak
    /// tanımladığımız için değişken kullanımı buraya karıştırmak metriği bozar.
    public decimal MonthlyValue => HasFlatFee
        ? Math.Round(Price / (int)Cycle, 2, MidpointRounding.AwayFromZero)
        : 0m;

    public string CycleText => Cycle switch
    {
        BillingCycle.Monthly => "Aylık",
        BillingCycle.Quarterly => "3 Aylık",
        BillingCycle.SemiAnnual => "6 Aylık",
        BillingCycle.Yearly => "Yıllık",
        _ => "?"
    };
}
