using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

public sealed class Subscription : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid PartyId { get; set; }
    public Party Party { get; set; } = default!;

    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = default!;

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? TrialEndsOn { get; set; }
    public DateOnly? CancelledOn { get; set; }

    /// <summary>Bir sonraki faturanın kesileceği tarih = bir sonraki dönemin BAŞLANGICI.</summary>
    public DateOnly NextBillingDate { get; set; }

    /// <summary>
    /// Faturalandırma çapa günü (1–31). AddMonths tek başına yetmez:
    /// 31 Ocak + 1 ay = 28 Şubat, sonra 28 Şubat + 1 ay = 28 Mart — gün KAYAR.
    /// Çapa ayrı saklanınca 31 Oca → 28 Şub → 31 Mar doğru ilerler.
    /// </summary>
    public int BillingAnchorDay { get; set; }

    /// <summary>Plan fiyatından farklı, cariye özel fiyat. Null ise plan fiyatı geçerli.</summary>
    public decimal? CustomPrice { get; set; }

    public decimal Quantity { get; set; } = 1m;      // 25 kullanıcılı lisans gibi
    public string? Notes { get; set; }

    public bool IsBillable => Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    public void Cancel(DateOnly on, bool immediately)
    {
        if (Status == SubscriptionStatus.Cancelled)
            throw new DomainException("Abonelik zaten iptal edilmiş.");

        CancelledOn = on;
        Status = SubscriptionStatus.Cancelled;
        // immediately=false → dönem sonuna kadar hizmet sürer
        EndDate = immediately ? on : NextBillingDate.AddDays(-1);
    }

    public string StatusText => Status switch
    {
        SubscriptionStatus.Trialing => "Deneme",
        SubscriptionStatus.Active => "Aktif",
        SubscriptionStatus.PastDue => "Vadesi Geçti",
        SubscriptionStatus.Paused => "Duraklatıldı",
        SubscriptionStatus.Cancelled => "İptal",
        _ => "?"
    };
}
