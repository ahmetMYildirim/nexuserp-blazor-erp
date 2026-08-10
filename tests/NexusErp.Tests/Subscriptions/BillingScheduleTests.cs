using NexusErp.Domain.Enums;
using NexusErp.Domain.Subscriptions;
using Shouldly;

namespace NexusErp.Tests.Subscriptions;

public class BillingScheduleTests
{
    /// <summary>
    /// Planın en ince noktası. Müşteri her ayın 31'inde (veya ayın son gününde)
    /// faturalanmayı bekler; kayan tarih yıl sonunda 3 gün fark ve şikâyet demektir.
    /// </summary>
    [Fact]
    public void Ayin_son_gunu_capasi_kaymaz()
    {
        const int anchor = 31;
        var d = new DateOnly(2026, 1, 31);

        d = BillingSchedule.NextPeriodStart(d, BillingCycle.Monthly, anchor);
        d.ShouldBe(new DateOnly(2026, 2, 28));        // Şubat kısa, kırpıldı

        d = BillingSchedule.NextPeriodStart(d, BillingCycle.Monthly, anchor);
        d.ShouldBe(new DateOnly(2026, 3, 31));        // çapa geri geldi ✓

        d = BillingSchedule.NextPeriodStart(d, BillingCycle.Monthly, anchor);
        d.ShouldBe(new DateOnly(2026, 4, 30));
    }

    [Fact]
    public void Artik_yil_subatta_29_olur()
    {
        BillingSchedule.NextPeriodStart(new DateOnly(2028, 1, 31), BillingCycle.Monthly, 31)
                       .ShouldBe(new DateOnly(2028, 2, 29));   // 2028 artık yıl
    }

    [Fact]
    public void Donem_bitisi_sonraki_baslangicin_bir_gun_oncesi()
    {
        BillingSchedule.PeriodEnd(new DateOnly(2026, 3, 1), BillingCycle.Monthly, 1)
                       .ShouldBe(new DateOnly(2026, 3, 31));
    }

    [Fact]
    public void Yillik_dongu_bir_yil_ekler()
    {
        BillingSchedule.NextPeriodStart(new DateOnly(2026, 3, 15), BillingCycle.Yearly, 15)
                       .ShouldBe(new DateOnly(2027, 3, 15));
    }

    [Fact]
    public void Uc_aylik_dongu_uc_ay_ekler()
    {
        BillingSchedule.NextPeriodStart(new DateOnly(2026, 1, 15), BillingCycle.Quarterly, 15)
                       .ShouldBe(new DateOnly(2026, 4, 15));
    }

    [Fact]
    public void Proration_kalan_gune_gore_hesaplanir()
    {
        // 1–31 Mart dönemi, 15 Mart'ta değişiklik → 17 gün kaldı (15 dahil)
        // 499 × 17/31 = 273,6452 → 273,65
        BillingSchedule.Prorate(499m, new DateOnly(2026, 3, 1),
                                new DateOnly(2026, 3, 31), new DateOnly(2026, 3, 15))
                       .ShouldBe(273.65m);
    }

    [Fact]
    public void Donem_basinda_proration_tam_tutar()
    {
        BillingSchedule.Prorate(499m, new DateOnly(2026, 3, 1),
                                new DateOnly(2026, 3, 31), new DateOnly(2026, 3, 1))
                       .ShouldBe(499m);
    }

    [Fact]
    public void Donem_bittikten_sonra_proration_sifir()
    {
        BillingSchedule.Prorate(499m, new DateOnly(2026, 3, 1),
                                new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 5))
                       .ShouldBe(0m);
    }

    [Fact]
    public void Plan_yukseltmesinde_fark_hesaplanir()
    {
        var start = new DateOnly(2026, 3, 1);
        var end = new DateOnly(2026, 3, 31);
        var change = new DateOnly(2026, 3, 15);

        var iade = BillingSchedule.Prorate(499m, start, end, change);    // eski plandan iade
        var ekUcret = BillingSchedule.Prorate(999m, start, end, change); // yeni plan için ek

        iade.ShouldBe(273.65m);      // 499 × 17/31
        ekUcret.ShouldBe(547.84m);   // 999 × 17/31
        (ekUcret - iade).ShouldBe(274.19m);
    }
}
