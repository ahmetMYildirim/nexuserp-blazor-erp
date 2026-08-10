using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Subscriptions;

/// <summary>
/// Abonelik takvimi. Saf fonksiyonlar — veri tabanı yok, testleri anında koşar.
/// </summary>
public static class BillingSchedule
{
    /// <summary>
    /// Dönem bitişi = bir sonraki dönem başlangıcının bir gün öncesi.
    /// 1 Mart'ta başlayan aylık dönem 31 Mart'ta biter (1 Nisan değil).
    /// </summary>
    public static DateOnly PeriodEnd(DateOnly periodStart, BillingCycle cycle, int anchorDay)
        => NextPeriodStart(periodStart, cycle, anchorDay).AddDays(-1);

    /// <summary>
    /// Bir sonraki dönem başlangıcı.
    ///
    /// ⚠️ AddMonths TEK BAŞINA YETMEZ — gün kalıcı olarak kayar:
    ///   31 Oca → 28 Şub → 28 Mar → 28 Nis ...   ✗
    /// Çapa günü ayrı saklanınca doğru ilerler:
    ///   31 Oca → 28 Şub → 31 Mar → 30 Nis ...   ✓
    /// (Stripe/Chargebee dokümanlarında "billing anchor" adıyla geçer.)
    /// </summary>
    public static DateOnly NextPeriodStart(DateOnly current, BillingCycle cycle, int anchorDay)
        => WithAnchorDay(current.AddMonths((int)cycle), anchorDay);

    private static DateOnly WithAnchorDay(DateOnly date, int anchorDay)
    {
        if (anchorDay is < 1 or > 31)
            throw new DomainException("Faturalandırma günü 1–31 aralığında olmalıdır.");

        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        return new DateOnly(date.Year, date.Month, Math.Min(anchorDay, daysInMonth));
    }

    /// <summary>
    /// Dönem ortası değişiklikte oransal tutar (proration).
    /// Kalan gün / toplam gün — her iki uç dahil.
    /// </summary>
    public static decimal Prorate(decimal fullPeriodAmount,
                                  DateOnly periodStart, DateOnly periodEnd, DateOnly changeDate)
    {
        if (periodEnd < periodStart)
            throw new DomainException("Dönem bitişi başlangıcından önce olamaz.");

        var totalDays = periodEnd.DayNumber - periodStart.DayNumber + 1;
        var remainingDays = periodEnd.DayNumber - changeDate.DayNumber + 1;

        if (remainingDays <= 0) return 0m;
        if (remainingDays >= totalDays) return fullPeriodAmount;

        return Math.Round(fullPeriodAmount * remainingDays / totalDays,
                          2, MidpointRounding.AwayFromZero);
    }
}
