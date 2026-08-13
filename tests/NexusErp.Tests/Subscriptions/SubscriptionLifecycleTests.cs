using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Subscriptions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Subscriptions;

/// <summary>
/// Abonelik yaşam döngüsü: oluşturma, deneme, duraklatma, plan değişikliği.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SubscriptionLifecycleTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();

    private sealed record Seed(Guid PartyId, Guid MonthlyPlanId, Guid YearlyPlanId);

    private async Task<Seed> SeedAsync(Guid tenant)
    {
        await using var db = fixture.CreateContext(tenant);

        var taxRate = new TaxRate
        {
            TenantId = tenant, Code = "KDV20", Name = "KDV %20", Rate = 0.20m,
            ValidFrom = new DateOnly(2020, 1, 1), IsDefault = true
        };
        var product = new Product
        {
            TenantId = tenant, Code = "ABN", Name = "Abonelik Hizmeti", Unit = "Ay",
            UnitPrice = 1_000m, TaxRateId = taxRate.Id
        };
        var party = new Party
        {
            TenantId = tenant, Code = "MUS8101", Title = "Abonelik Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var monthly = new Plan
        {
            TenantId = tenant, Code = "AYLIK", Name = "Aylık Paket",
            Price = 1_000m, Currency = "TRY", Cycle = BillingCycle.Monthly,
            TrialDays = 14, ProductId = product.Id
        };
        var yearly = new Plan
        {
            TenantId = tenant, Code = "YILLIK", Name = "Yıllık Paket",
            Price = 10_000m, Currency = "TRY", Cycle = BillingCycle.Yearly,
            TrialDays = 0, ProductId = product.Id
        };

        db.TaxRates.Add(taxRate);
        db.Products.Add(product);
        db.Parties.Add(party);
        db.Plans.AddRange(monthly, yearly);
        await db.SaveChangesAsync();

        return new Seed(party.Id, monthly.Id, yearly.Id);
    }

    private SubscriptionService Service(Guid tenant) => new(fixture.CreateFactory(tenant));

    // ============================================================ OLUŞTURMA

    [Fact]
    public async Task Deneme_sureli_plan_trialing_baslar_ve_ilk_fatura_deneme_sonrasi()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        var start = new DateOnly(2026, 3, 10);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId, StartDate = start
        });

        await using var db = fixture.CreateContext(tenant);
        var sub = await db.Subscriptions.FirstAsync(s => s.Id == id);

        sub.Status.ShouldBe(SubscriptionStatus.Trialing);
        sub.TrialEndsOn.ShouldBe(start.AddDays(14));
        // Deneme boyunca fatura kesilmez; ilk fatura bitimin ertesi günü
        sub.NextBillingDate.ShouldBe(start.AddDays(15));
    }

    [Fact]
    public async Task Denemesiz_plan_aktif_baslar_ve_ilk_fatura_bugun()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        var start = new DateOnly(2026, 3, 10);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.YearlyPlanId, StartDate = start
        });

        await using var db = fixture.CreateContext(tenant);
        var sub = await db.Subscriptions.FirstAsync(s => s.Id == id);

        sub.Status.ShouldBe(SubscriptionStatus.Active);
        sub.TrialEndsOn.ShouldBeNull();
        sub.NextBillingDate.ShouldBe(start);
    }

    [Fact]
    public async Task Capa_gunu_verilmezse_baslangic_gununden_alinir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.YearlyPlanId,
            StartDate = new DateOnly(2026, 1, 31)
        });

        await using var db = fixture.CreateContext(tenant);
        (await db.Subscriptions.FirstAsync(s => s.Id == id)).BillingAnchorDay.ShouldBe(31);
    }

    [Fact]
    public async Task Pasif_plana_abonelik_acilamaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        await using (var db = fixture.CreateContext(tenant))
        {
            var plan = await db.Plans.FirstAsync(p => p.Id == seed.MonthlyPlanId);
            plan.IsActive = false;
            await db.SaveChangesAsync();
        }

        var ex = await Should.ThrowAsync<DomainException>(() =>
            Service(tenant).CreateAsync(new SubscriptionForm
            {
                PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId,
                StartDate = new DateOnly(2026, 3, 1)
            }));

        ex.Message.ShouldContain("pasif");
    }

    // ============================================================ ÖNİZLEME

    [Fact]
    public async Task Onizleme_ilk_fatura_tarihini_ve_tutarini_verir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        var start = new DateOnly(2026, 3, 10);

        var preview = await Service(tenant).PreviewAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId,
            StartDate = start, Quantity = 3m
        });

        preview.TrialEndsOn.ShouldBe(start.AddDays(14));
        preview.FirstBillingDate.ShouldBe(start.AddDays(15));
        preview.FirstAmount.ShouldBe(3_000m);            // 1.000 × 3
        preview.CycleText.ShouldBe("Aylık");
    }

    // ============================================================ DURAKLATMA

    [Fact]
    public async Task Duraklat_ve_surdur_gecmis_donemleri_faturalamaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.YearlyPlanId,
            StartDate = new DateOnly(2026, 1, 15)
        });

        // Yıllık plan → sonraki fatura 15 Oca 2026. Duraklat, 3 yıl sonra sürdür.
        await Service(tenant).PauseAsync(id, new DateOnly(2026, 2, 1));

        await using (var db = fixture.CreateContext(tenant))
        {
            var paused = await db.Subscriptions.FirstAsync(s => s.Id == id);
            paused.Status.ShouldBe(SubscriptionStatus.Paused);
            paused.PausedOn.ShouldBe(new DateOnly(2026, 2, 1));
        }

        await Service(tenant).ResumeAsync(id, new DateOnly(2029, 6, 20));

        await using var db2 = fixture.CreateContext(tenant);
        var resumed = await db2.Subscriptions.FirstAsync(s => s.Id == id);

        resumed.Status.ShouldBe(SubscriptionStatus.Active);
        resumed.PausedOn.ShouldBeNull();
        // ⚠️ Kritik: takvim ileri sarılmalı. Sarılmasaydı 2026–2029 arası her yıl
        // için geriye dönük fatura kesilir ve müşteri hizmet almadığı yıllara borçlanırdı.
        resumed.NextBillingDate.ShouldBeGreaterThanOrEqualTo(new DateOnly(2029, 6, 20));
        resumed.NextBillingDate.Day.ShouldBe(15);        // çapa günü korundu
    }

    [Fact]
    public async Task Iptal_edilmis_abonelik_duraklatilamaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.YearlyPlanId,
            StartDate = new DateOnly(2026, 1, 15)
        });

        await Service(tenant).CancelAsync(id, new DateOnly(2026, 2, 1), immediately: true);

        await Should.ThrowAsync<DomainException>(() =>
            Service(tenant).PauseAsync(id, new DateOnly(2026, 3, 1)));
    }

    // ============================================================ PLAN DEĞİŞİKLİĞİ

    [Fact]
    public async Task Upgrade_onizlemesi_pozitif_fark_verir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        // Aylık 1.000 ile başla, dönem 1–31 Mart
        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId,
            StartDate = new DateOnly(2026, 3, 1), TrialDays = 0
        });

        await using (var db = fixture.CreateContext(tenant))
        {
            // Bir dönem ilerlet: sonraki fatura 1 Nisan olsun
            var s = await db.Subscriptions.FirstAsync(x => x.Id == id);
            s.NextBillingDate = new DateOnly(2026, 4, 1);
            await db.SaveChangesAsync();
        }

        // 16 Mart'ta yıllık plana geç (10.000)
        var preview = await Service(tenant).PreviewPlanChangeAsync(
            id, seed.YearlyPlanId, new DateOnly(2026, 3, 16));

        preview.CurrentPlanName.ShouldBe("Aylık Paket");
        preview.NewPlanName.ShouldBe("Yıllık Paket");
        preview.PeriodStart.ShouldBe(new DateOnly(2026, 3, 1));
        preview.PeriodEnd.ShouldBe(new DateOnly(2026, 3, 31));
        preview.IsUpgrade.ShouldBeTrue();
        preview.ProrationAmount.ShouldBeGreaterThan(0m);
        preview.Explanation.ShouldContain("fark faturalanacak");
    }

    [Fact]
    public async Task Downgrade_onizlemesi_negatif_fark_verir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.YearlyPlanId,
            StartDate = new DateOnly(2026, 1, 1)
        });

        await using (var db = fixture.CreateContext(tenant))
        {
            var s = await db.Subscriptions.FirstAsync(x => x.Id == id);
            s.NextBillingDate = new DateOnly(2027, 1, 1);
            await db.SaveChangesAsync();
        }

        var preview = await Service(tenant).PreviewPlanChangeAsync(
            id, seed.MonthlyPlanId, new DateOnly(2026, 7, 1));

        preview.IsUpgrade.ShouldBeFalse();
        preview.ProrationAmount.ShouldBeLessThan(0m);
        preview.Explanation.ShouldContain("alacak oluşacak");
    }

    [Fact]
    public async Task Plan_degisikligi_uygulanir_ve_takvim_yeni_donguye_gore_kurulur()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId,
            StartDate = new DateOnly(2026, 3, 1), TrialDays = 0
        });

        await Service(tenant).ChangePlanAsync(id, seed.YearlyPlanId, new DateOnly(2026, 3, 16));

        await using var db = fixture.CreateContext(tenant);
        var sub = await db.Subscriptions.Include(s => s.Plan).FirstAsync(s => s.Id == id);

        sub.PlanId.ShouldBe(seed.YearlyPlanId);
        // Yıllık döngü: 16 Mart 2026 + 12 ay, çapa günü 1
        sub.NextBillingDate.ShouldBe(new DateOnly(2027, 3, 1));
    }

    [Fact]
    public async Task Ayni_plana_gecilemez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId,
            StartDate = new DateOnly(2026, 3, 1), TrialDays = 0
        });

        var ex = await Should.ThrowAsync<DomainException>(() =>
            Service(tenant).ChangePlanAsync(id, seed.MonthlyPlanId, new DateOnly(2026, 3, 16)));

        ex.Message.ShouldContain("zaten bu planda");
    }

    // ============================================================ DETAY

    [Fact]
    public async Task Detay_abonelik_ve_faturalarini_tek_cagrida_dondurur()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Service(tenant).CreateAsync(new SubscriptionForm
        {
            PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId,
            StartDate = new DateOnly(2026, 3, 1), Quantity = 2m, TrialDays = 0
        });

        var detail = await Service(tenant).GetDetailAsync(id);

        detail.ShouldNotBeNull();
        detail.PartyTitle.ShouldBe("Abonelik Testi A.Ş.");
        detail.PlanName.ShouldBe("Aylık Paket");
        detail.CycleText.ShouldBe("Aylık");
        detail.PeriodAmount.ShouldBe(2_000m);        // 1.000 × 2
        detail.MonthlyValue.ShouldBe(2_000m);        // aylık döngü → aynı
        detail.InvoiceCount.ShouldBe(0);             // henüz faturalanmadı
        detail.CanBill.ShouldBeTrue();
    }

    [Fact]
    public async Task Olmayan_abonelik_detayi_null_doner()
        => (await Service(NewTenant()).GetDetailAsync(Guid.CreateVersion7())).ShouldBeNull();
}
