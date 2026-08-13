using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Subscriptions;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Subscriptions;

/// <summary>
/// Churn analizi: kaç abonelik gitti, NEDEN gitti, aylık ne kadar gelir kaybedildi.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ChurnTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 6, 30);

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
            TenantId = tenant, Code = "ABN", Name = "Bakım", Unit = "Ay",
            UnitPrice = 1_000m, TaxRateId = taxRate.Id
        };
        var party = new Party
        {
            TenantId = tenant, Code = "MUS9301", Title = "Churn Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var monthly = new Plan
        {
            TenantId = tenant, Code = "AYLIK", Name = "Aylık", Price = 1_000m,
            Currency = "TRY", Cycle = BillingCycle.Monthly, ProductId = product.Id
        };
        var yearly = new Plan
        {
            TenantId = tenant, Code = "YILLIK", Name = "Yıllık", Price = 12_000m,
            Currency = "TRY", Cycle = BillingCycle.Yearly, ProductId = product.Id
        };

        db.TaxRates.Add(taxRate);
        db.Products.Add(product);
        db.Parties.Add(party);
        db.Plans.AddRange(monthly, yearly);
        await db.SaveChangesAsync();

        return new Seed(party.Id, monthly.Id, yearly.Id);
    }

    private async Task<Guid> AddCancelledAsync(
        Guid tenant, Seed seed, Guid planId, CancellationReason reason, DateOnly cancelledOn,
        decimal quantity = 1m)
    {
        await using var db = fixture.CreateContext(tenant);
        var sub = new Subscription
        {
            TenantId = tenant, PartyId = seed.PartyId, PlanId = planId,
            Status = SubscriptionStatus.Cancelled,
            StartDate = new DateOnly(2026, 1, 1),
            NextBillingDate = new DateOnly(2026, 7, 1),
            BillingAnchorDay = 1,
            Quantity = quantity,
            CancelledOn = cancelledOn,
            CancellationReason = reason
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private SubscriptionService Service(Guid tenant) => new(fixture.CreateFactory(tenant));

    [Fact]
    public async Task Iptal_yoksa_bos_analiz_doner()
    {
        var churn = await Service(NewTenant()).GetChurnAsync(From, To);

        churn.CancelledCount.ShouldBe(0);
        churn.LostMrr.ShouldBe(0m);
        churn.Reasons.ShouldBeEmpty();
        churn.TopReason.ShouldBeNull();
        churn.Summary.ShouldContain("iptal yok");
    }

    [Fact]
    public async Task Sebepler_gruplanir_ve_en_siki_one_cikar()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        await AddCancelledAsync(tenant, seed, seed.MonthlyPlanId,
                                CancellationReason.TooExpensive, new DateOnly(2026, 6, 5));
        await AddCancelledAsync(tenant, seed, seed.MonthlyPlanId,
                                CancellationReason.TooExpensive, new DateOnly(2026, 6, 10));
        await AddCancelledAsync(tenant, seed, seed.MonthlyPlanId,
                                CancellationReason.NotUsing, new DateOnly(2026, 6, 15));

        var churn = await Service(tenant).GetChurnAsync(From, To);

        churn.CancelledCount.ShouldBe(3);
        churn.Reasons.Count.ShouldBe(2);
        churn.TopReason!.Reason.ShouldBe(CancellationReason.TooExpensive);
        churn.TopReason.Count.ShouldBe(2);
        churn.TopReason.Label.ShouldBe("Fiyat yüksek");
        churn.Summary.ShouldContain("Fiyat yüksek");
    }

    /// <summary>
    /// Kayıp MRR aya normalize edilmeli: 12.000 TL/yıl bir plan aylık 1.000 kaybettirir,
    /// 12.000 değil. Bölünmezse rakam 12 kat şişer.
    /// </summary>
    [Fact]
    public async Task Kayip_mrr_aya_normalize_edilir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        await AddCancelledAsync(tenant, seed, seed.YearlyPlanId,
                                CancellationReason.NotUsing, new DateOnly(2026, 6, 5));

        var churn = await Service(tenant).GetChurnAsync(From, To);

        churn.LostMrr.ShouldBe(1_000m);   // 12.000 / 12
    }

    [Fact]
    public async Task Miktar_kayip_mrri_carpar()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        await AddCancelledAsync(tenant, seed, seed.MonthlyPlanId,
                                CancellationReason.BusinessClosed, new DateOnly(2026, 6, 5),
                                quantity: 5m);

        (await Service(tenant).GetChurnAsync(From, To)).LostMrr.ShouldBe(5_000m);
    }

    [Fact]
    public async Task Aralik_disindaki_iptaller_sayilmaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        await AddCancelledAsync(tenant, seed, seed.MonthlyPlanId,
                                CancellationReason.NotUsing, new DateOnly(2026, 5, 20));
        await AddCancelledAsync(tenant, seed, seed.MonthlyPlanId,
                                CancellationReason.NotUsing, new DateOnly(2026, 7, 3));

        (await Service(tenant).GetChurnAsync(From, To)).CancelledCount.ShouldBe(0);
    }

    [Fact]
    public async Task Iptal_sebebi_kaydedilir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        await using (var db = fixture.CreateContext(tenant))
        {
            db.Subscriptions.Add(new Subscription
            {
                TenantId = tenant, PartyId = seed.PartyId, PlanId = seed.MonthlyPlanId,
                Status = SubscriptionStatus.Active,
                StartDate = new DateOnly(2026, 1, 1),
                NextBillingDate = new DateOnly(2026, 7, 1),
                BillingAnchorDay = 1
            });
            await db.SaveChangesAsync();
        }

        Guid id;
        await using (var db = fixture.CreateContext(tenant))
            id = (await db.Subscriptions.FirstAsync()).Id;

        await Service(tenant).CancelAsync(
            id, new DateOnly(2026, 6, 12), immediately: false,
            CancellationReason.SwitchedToCompetitor, "  Rakip %30 ucuz teklif verdi  ");

        await using var check = fixture.CreateContext(tenant);
        var sub = await check.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == id);

        sub.CancellationReason.ShouldBe(CancellationReason.SwitchedToCompetitor);
        sub.CancellationNote.ShouldBe("Rakip %30 ucuz teklif verdi");   // trim edilmiş
    }

    [Fact]
    public async Task Baska_tenantin_iptalleri_gorunmez()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var seedA = await SeedAsync(tenantA);
        await SeedAsync(tenantB);

        await AddCancelledAsync(tenantA, seedA, seedA.MonthlyPlanId,
                                CancellationReason.NotUsing, new DateOnly(2026, 6, 5));

        (await Service(tenantB).GetChurnAsync(From, To)).CancelledCount.ShouldBe(0);
    }
}
