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

    /// <summary>
    /// MRR katkısı — dönem ne olursa olsun aya normalize edilir.
    /// 12.000 TL/yıl bir plan MRR'a 1.000 TL katar. Yanlış hesaplarsan rakamlar 12 kat şişer.
    /// </summary>
    public decimal MonthlyValue =>
        Math.Round(Price / (int)Cycle, 2, MidpointRounding.AwayFromZero);

    public string CycleText => Cycle switch
    {
        BillingCycle.Monthly => "Aylık",
        BillingCycle.Quarterly => "3 Aylık",
        BillingCycle.SemiAnnual => "6 Aylık",
        BillingCycle.Yearly => "Yıllık",
        _ => "?"
    };
}
