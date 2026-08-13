using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Subscriptions;

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

    /// <summary>Duraklatma tarihi. Null ise abonelik hiç duraklatılmamış.</summary>
    public DateOnly? PausedOn { get; set; }

    /// <summary>Neden iptal edildi — churn analizinin girdisi.</summary>
    public CancellationReason CancellationReason { get; set; } = CancellationReason.Unspecified;

    /// <summary>Kullanıcının serbest metin açıklaması (opsiyonel).</summary>
    public string? CancellationNote { get; set; }

    // --- Dunning (ödenmeyen fatura takibi) ---

    /// <summary>Aboneliğin ne zamandan beri ödenmemiş faturası var.</summary>
    public DateOnly? PastDueSince { get; set; }

    /// <summary>
    /// Gönderilen son hatırlatma seviyesi (0 = hiç). Aynı hatırlatmanın her gün
    /// tekrar gönderilmesini engeller — işçi günde bir çalışıyor.
    /// </summary>
    public int DunningLevel { get; set; }

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

    /// <summary>Deneme süresi devam ediyor mu?</summary>
    public bool IsInTrial(DateOnly on) =>
        Status == SubscriptionStatus.Trialing && TrialEndsOn is { } t && on <= t;

    public void Cancel(DateOnly on, bool immediately,
                       CancellationReason reason = CancellationReason.Unspecified,
                       string? note = null)
    {
        if (Status == SubscriptionStatus.Cancelled)
            throw new DomainException("Abonelik zaten iptal edilmiş.");

        CancelledOn = on;
        Status = SubscriptionStatus.Cancelled;
        CancellationReason = reason;
        CancellationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        // immediately=false → dönem sonuna kadar hizmet sürer
        EndDate = immediately ? on : NextBillingDate.AddDays(-1);
    }

    // ------------------------------------------------------------------
    // Yaşam döngüsü — durum geçişleri entity'nin sorumluluğu
    // ------------------------------------------------------------------

    /// <summary>
    /// Aboneliği duraklatır. Faturalandırma durur ama abonelik silinmez;
    /// devam edildiğinde takvim ileri sarılır (geçmişe dönük fatura kesilmez).
    /// </summary>
    public void Pause(DateOnly on)
    {
        if (Status is SubscriptionStatus.Cancelled)
            throw new DomainException("İptal edilmiş abonelik duraklatılamaz.");
        if (Status is SubscriptionStatus.Paused)
            throw new DomainException("Abonelik zaten duraklatılmış.");

        Status = SubscriptionStatus.Paused;
        PausedOn = on;
    }

    /// <summary>
    /// Duraklatılmış aboneliği sürdürür.
    /// ⚠️ NextBillingDate GEÇMİŞTE kalmışsa ileri sarılır — aksi halde devam eder
    /// etmez duraklama boyunca birikmiş tüm dönemler için fatura kesilir ve
    /// müşteri hizmet almadığı aylar için borçlandırılır.
    /// </summary>
    public void Resume(DateOnly on)
    {
        if (Status != SubscriptionStatus.Paused)
            throw new DomainException("Yalnızca duraklatılmış abonelik sürdürülebilir.");

        Status = SubscriptionStatus.Active;
        PausedOn = null;

        while (NextBillingDate < on)
            NextBillingDate = BillingSchedule.NextPeriodStart(
                NextBillingDate, Plan.Cycle, BillingAnchorDay);
    }

    // ------------------------------------------------------------------
    // Dunning — ödenmeyen fatura süreci
    // ------------------------------------------------------------------

    /// <summary>
    /// Hatırlatma eşikleri (gün). Ödenmeyen faturanın vadesinden bu kadar gün
    /// sonra sırayla hatırlatma gönderilir.
    /// </summary>
    public static readonly int[] DunningDays = [3, 7, 14];

    /// <summary>Bu eşikten sonra ödeme gelmezse abonelik askıya alınır.</summary>
    public const int SuspendAfterDays = 21;

    /// <summary>
    /// Ödenmemiş vadesi geçmiş fatura tespit edildi.
    /// ⚠️ İdempotent: zaten PastDue ise <see cref="PastDueSince"/> DEĞİŞMEZ.
    /// Aksi halde her tur tarih ileri kayar, gecikme günü hep 0 çıkar ve
    /// hiçbir hatırlatma eşiği asla dolmaz.
    /// </summary>
    public void MarkPastDue(DateOnly on)
    {
        if (Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Paused) return;

        if (Status != SubscriptionStatus.PastDue)
        {
            Status = SubscriptionStatus.PastDue;
            PastDueSince = on;
            DunningLevel = 0;
        }

        PastDueSince ??= on;
    }

    /// <summary>Borç kapandı — abonelik normale döner ve sayaç sıfırlanır.</summary>
    public void ClearPastDue()
    {
        if (Status == SubscriptionStatus.PastDue) Status = SubscriptionStatus.Active;
        PastDueSince = null;
        DunningLevel = 0;
    }

    /// <summary>Gecikme gün sayısı. PastDue değilse 0.</summary>
    public int DaysPastDue(DateOnly on) =>
        PastDueSince is { } since ? Math.Max(0, on.DayNumber - since.DayNumber) : 0;

    /// <summary>
    /// Bu turda gönderilmesi gereken hatırlatma seviyesi. Yoksa null.
    /// Seviye zaten gönderilmişse tekrar üretmez.
    /// </summary>
    public int? NextDunningLevel(DateOnly on)
    {
        if (Status != SubscriptionStatus.PastDue) return null;

        var days = DaysPastDue(on);

        // En yüksek dolmuş eşiği bul; henüz gönderilmemişse onu döndür
        var reached = 0;
        for (var i = 0; i < DunningDays.Length; i++)
            if (days >= DunningDays[i]) reached = i + 1;

        return reached > DunningLevel ? reached : null;
    }

    public bool ShouldSuspend(DateOnly on) =>
        Status == SubscriptionStatus.PastDue && DaysPastDue(on) >= SuspendAfterDays;

    /// <summary>
    /// Ödeme gelmediği için hizmeti durdurur. İptal DEĞİL — borç ödenirse
    /// <see cref="Resume"/> ile geri açılabilir.
    /// </summary>
    public void Suspend(DateOnly on)
    {
        if (Status is SubscriptionStatus.Cancelled)
            throw new DomainException("İptal edilmiş abonelik askıya alınamaz.");

        Status = SubscriptionStatus.Paused;
        PausedOn = on;
    }

    /// <summary>Deneme süresi bitti — ilk gerçek dönem başlıyor.</summary>
    public void ActivateFromTrial()
    {
        if (Status != SubscriptionStatus.Trialing)
            throw new DomainException("Abonelik deneme sürecinde değil.");

        Status = SubscriptionStatus.Active;
    }

    /// <summary>
    /// Plan değişikliği. Dönem ortasındaysa kullanılmayan kısım için alacak,
    /// yeni plan için borç doğar; fark <see cref="ProrationAmount"/> ile hesaplanır.
    ///
    /// ⚠️ Yeni planın döngüsü farklıysa (aylık → yıllık) çapa günü korunur ama
    /// bir sonraki fatura tarihi yeni döngüye göre yeniden kurulur.
    /// </summary>
    public void ChangePlan(Plan newPlan, DateOnly on, decimal? newCustomPrice = null)
    {
        if (Status is SubscriptionStatus.Cancelled)
            throw new DomainException("İptal edilmiş aboneliğin planı değiştirilemez.");
        if (newPlan.Id == PlanId && newCustomPrice == CustomPrice)
            throw new DomainException("Abonelik zaten bu planda.");
        if (!newPlan.IsActive)
            throw new DomainException($"'{newPlan.Name}' planı pasif, atanamaz.");

        PlanId = newPlan.Id;
        Plan = newPlan;
        CustomPrice = newCustomPrice;

        // Dönem başı değişmiyor; bir sonraki fatura yeni döngüye göre hesaplanır.
        NextBillingDate = BillingSchedule.NextPeriodStart(on, newPlan.Cycle, BillingAnchorDay);
    }

    /// <summary>
    /// Plan değişiminde dönem ortası farkı. Pozitif = müşteri borçlanır (upgrade),
    /// negatif = alacaklanır (downgrade).
    /// </summary>
    public static decimal ProrationAmount(
        decimal oldPeriodPrice, decimal newPeriodPrice,
        DateOnly periodStart, DateOnly periodEnd, DateOnly changeDate)
    {
        var unusedOld = BillingSchedule.Prorate(oldPeriodPrice, periodStart, periodEnd, changeDate);
        var chargedNew = BillingSchedule.Prorate(newPeriodPrice, periodStart, periodEnd, changeDate);
        return Math.Round(chargedNew - unusedOld, 2, MidpointRounding.AwayFromZero);
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
