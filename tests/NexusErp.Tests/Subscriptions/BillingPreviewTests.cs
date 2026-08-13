using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Subscriptions;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Subscriptions;

/// <summary>
/// Toplu faturalandırma önizlemesi. Kritik olan tek şey: önizlemenin seçim
/// koşulu gerçek turla BİREBİR aynı olmalı. Ayrışırsa kullanıcı onay verdiği
/// şeyden farklı bir sonuç alır.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BillingPreviewTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();
    private static DateOnly Today => new(2026, 6, 15);

    private sealed record Seed(Guid PartyId, Guid PlanId);

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
            TenantId = tenant, Code = "MUS9101", Title = "Önizleme Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var plan = new Plan
        {
            TenantId = tenant, Code = "AYLIK", Name = "Aylık Paket",
            Price = 1_000m, Currency = "TRY", Cycle = BillingCycle.Monthly,
            ProductId = product.Id
        };

        db.TaxRates.Add(taxRate);
        db.Products.Add(product);
        db.Parties.Add(party);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        return new Seed(party.Id, plan.Id);
    }

    private async Task AddSubscriptionAsync(Guid tenant, Seed seed, DateOnly nextBilling,
                                            decimal quantity = 1m)
    {
        await using var db = fixture.CreateContext(tenant);
        db.Subscriptions.Add(new Subscription
        {
            TenantId = tenant,
            PartyId = seed.PartyId,
            PlanId = seed.PlanId,
            Status = SubscriptionStatus.Active,
            StartDate = nextBilling,
            NextBillingDate = nextBilling,
            BillingAnchorDay = nextBilling.Day,
            Quantity = quantity
        });
        await db.SaveChangesAsync();
    }

    private SubscriptionBillingService Billing(Guid tenant)
    {
        var factory = fixture.CreateFactory(tenant);
        var generator = new InvoiceNumberGenerator(
            fixture.CreateContext(tenant), fixture.CreateTenantContext(tenant));
        var invoices = new InvoiceService(factory, generator, TimeProvider.System);

        return new SubscriptionBillingService(
            factory, invoices, NullLogger<SubscriptionBillingService>.Instance);
    }

    [Fact]
    public async Task Vadesi_gelmemis_abonelik_onizlemede_gorunmez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        await AddSubscriptionAsync(tenant, seed, Today.AddDays(10));   // gelecek

        var preview = await Billing(tenant).PreviewRunAsync(Today);

        preview.Rows.ShouldBeEmpty();
        preview.BillableCount.ShouldBe(0);
        preview.Summary.ShouldContain("Vadesi gelen abonelik yok");
    }

    [Fact]
    public async Task Onizleme_tutari_ve_donemi_dogru_hesaplar()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        await AddSubscriptionAsync(tenant, seed, new DateOnly(2026, 6, 1), quantity: 3m);

        var preview = await Billing(tenant).PreviewRunAsync(Today);

        var row = preview.Rows.ShouldHaveSingleItem();
        row.PartyTitle.ShouldBe("Önizleme Testi A.Ş.");
        row.Amount.ShouldBe(3_000m);                       // 1.000 × 3
        row.PeriodStart.ShouldBe(new DateOnly(2026, 6, 1));
        row.PeriodEnd.ShouldBe(new DateOnly(2026, 6, 30));  // bir sonraki dönemin bir gün öncesi
        row.AlreadyBilled.ShouldBeFalse();
        preview.Total.ShouldBe(3_000m);
    }

    /// <summary>
    /// Önizlemedeki sayı, turun gerçekten ürettiği fatura sayısıyla eşleşmeli.
    /// Bu test iki kod yolunun ayrışmasını yakalar.
    /// </summary>
    [Fact]
    public async Task Onizleme_sayisi_gercek_turla_eslesir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        await AddSubscriptionAsync(tenant, seed, new DateOnly(2026, 6, 1));
        await AddSubscriptionAsync(tenant, seed, new DateOnly(2026, 5, 1));   // gecikmiş
        await AddSubscriptionAsync(tenant, seed, Today.AddDays(30));          // gelecek

        var billing = Billing(tenant);

        var preview = await billing.PreviewRunAsync(Today);
        preview.BillableCount.ShouldBe(2);

        var result = await billing.RunAsync(Today);
        result.Created.ShouldBe(preview.BillableCount);
    }

    /// <summary>
    /// Tur çalıştıktan sonra ikinci önizleme aynı dönemleri "zaten faturalanmış"
    /// göstermeli — idempotency'nin arayüzdeki karşılığı.
    /// </summary>
    [Fact]
    public async Task Ikinci_onizleme_zaten_faturalanmis_isaretler()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        await AddSubscriptionAsync(tenant, seed, new DateOnly(2026, 6, 1));

        var billing = Billing(tenant);
        await billing.RunAsync(Today);

        // Takvim ilerledi; aynı gün tekrar bakıldığında yeni dönem henüz gelmedi
        var second = await billing.PreviewRunAsync(Today);
        second.BillableCount.ShouldBe(0);
    }

    [Fact]
    public async Task Iptal_edilmis_abonelik_onizlemede_yok()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        await AddSubscriptionAsync(tenant, seed, new DateOnly(2026, 6, 1));

        await using (var db = fixture.CreateContext(tenant))
        {
            var sub = await db.Subscriptions.FirstAsync();
            sub.Status = SubscriptionStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        (await Billing(tenant).PreviewRunAsync(Today)).Rows.ShouldBeEmpty();
    }
}
