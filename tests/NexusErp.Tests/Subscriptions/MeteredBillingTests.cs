using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NexusErp.Application.Accounting;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Subscriptions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Subscriptions;

/// <summary>
/// Kullanım bazlı faturalandırma. Buradaki testlerin hepsi PARA hatası yakalar:
/// çift sayılan kullanım, kaybolan geç kayıt, ikinci kez düşülen ücretsiz kota.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class MeteredBillingTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();

    private sealed record Seed(Guid PartyId, Guid PlanId, Guid SubscriptionId);

    private async Task<Seed> SeedAsync(
        Guid tenant, BillingModel model,
        decimal flatPrice = 1_000m, decimal included = 100m, decimal overage = 2m,
        decimal quantity = 1m, DateOnly? nextBilling = null)
    {
        var next = nextBilling ?? new DateOnly(2026, 7, 1);

        await using var db = fixture.CreateContext(tenant);

        var taxRate = new TaxRate
        {
            TenantId = tenant, Code = "KDV20", Name = "KDV %20", Rate = 0.20m,
            ValidFrom = new DateOnly(2020, 1, 1), IsDefault = true
        };
        var product = new Product
        {
            TenantId = tenant, Code = "SMS", Name = "SMS Paketi", Unit = "Ay",
            UnitPrice = flatPrice, TaxRateId = taxRate.Id
        };
        var party = new Party
        {
            TenantId = tenant, Code = "MUS7001", Title = "Kullanım Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var plan = new Plan
        {
            TenantId = tenant, Code = "SMS-PLAN", Name = "SMS Planı",
            Price = flatPrice, Currency = "TRY", Cycle = BillingCycle.Monthly,
            ProductId = product.Id,
            BillingModel = model,
            UsageUnitName = "SMS",
            IncludedUnits = included,
            OveragePrice = overage
        };
        var sub = new Subscription
        {
            TenantId = tenant,
            PartyId = party.Id,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            StartDate = next.AddMonths(-1),
            NextBillingDate = next,
            BillingAnchorDay = next.Day,
            Quantity = quantity
        };

        db.TaxRates.Add(taxRate);
        db.Products.Add(product);
        db.Parties.Add(party);
        db.Plans.Add(plan);
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();

        return new Seed(party.Id, plan.Id, sub.Id);
    }

    private SubscriptionBillingService Billing(Guid tenant)
    {
        var factory = fixture.CreateFactory(tenant);
        var generator = new InvoiceNumberGenerator(
            fixture.CreateContext(tenant), fixture.CreateTenantContext(tenant));
        fixture.SeedChartOfAccounts(tenant);
        var invoices = new InvoiceService(factory, generator, TimeProvider.System,
                                          new AutoPostingService(generator));

        return new SubscriptionBillingService(
            factory, invoices, NullLogger<SubscriptionBillingService>.Instance);
    }

    private UsageService Usage(Guid tenant)
        => new(fixture.CreateFactory(tenant), TimeProvider.System);

    private async Task AddUsageAsync(Guid tenant, Guid subId, DateOnly on, decimal qty,
                                     string? externalId = null)
        => await Usage(tenant).RecordAsync(new UsageEntry(subId, qty, on, "test", externalId));

    // ------------------------------------------------------------------

    [Fact]
    public async Task Kota_asilmazsa_kullanim_satiri_olusmaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Hybrid, included: 100m);

        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 10), 40m);
        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 20), 50m);

        await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));

        await using var db = fixture.CreateContext(tenant);
        var invoice = await db.Invoices.Include(i => i.Lines).AsNoTracking()
                              .FirstAsync(i => i.SubscriptionId == seed.SubscriptionId);

        invoice.Lines.Count.ShouldBe(1);                 // yalnızca sabit ücret
        invoice.TaxBaseTotal.ShouldBe(1_000m);

        // ⚠️ Kota içinde kalan kayıtlar da DAMGALANMALI — yoksa sonraki ay tekrar
        // toplanır ve kotayı ikinci kez tüketirler.
        var unbilled = await db.UsageRecords.AsNoTracking()
            .CountAsync(u => u.SubscriptionId == seed.SubscriptionId && u.InvoiceId == null);
        unbilled.ShouldBe(0);
    }

    [Fact]
    public async Task Kota_asimi_ayri_satir_olarak_faturalanir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Hybrid, included: 100m, overage: 2m);

        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 10), 250m);

        await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));

        await using var db = fixture.CreateContext(tenant);
        var invoice = await db.Invoices.Include(i => i.Lines).AsNoTracking()
                              .FirstAsync(i => i.SubscriptionId == seed.SubscriptionId);

        invoice.Lines.Count.ShouldBe(2);

        var usageLine = invoice.Lines.Single(l => l.Unit == "SMS");
        usageLine.Quantity.ShouldBe(150m);               // 250 − 100 kota
        usageLine.UnitPrice.ShouldBe(2m);

        // 1.000 sabit + 300 kullanım
        invoice.TaxBaseTotal.ShouldBe(1_300m);
        invoice.GrandTotal.ShouldBe(1_560m);             // + %20 KDV
    }

    [Fact]
    public async Task Kota_abonelik_miktariyla_carpilir()
    {
        var tenant = NewTenant();
        // 5 lisans × lisans başına 100 SMS = 500 SMS ücretsiz
        var seed = await SeedAsync(tenant, BillingModel.Hybrid, included: 100m, quantity: 5m);

        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 5), 600m);

        await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));

        await using var db = fixture.CreateContext(tenant);
        var invoice = await db.Invoices.Include(i => i.Lines).AsNoTracking()
                              .FirstAsync(i => i.SubscriptionId == seed.SubscriptionId);

        invoice.Lines.Single(l => l.Unit == "SMS").Quantity.ShouldBe(100m);   // 600 − 500
    }

    [Fact]
    public async Task Saf_kullanim_planinda_kullanim_yoksa_fatura_kesilmez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Metered, included: 0m);

        var result = await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));

        result.Created.ShouldBe(0);

        await using var db = fixture.CreateContext(tenant);
        (await db.Invoices.AnyAsync(i => i.SubscriptionId == seed.SubscriptionId))
            .ShouldBeFalse();

        // ⚠️ Fatura kesilmese de takvim İLERLEMELİ, yoksa abonelik sonsuza kadar
        // "vadesi gelmiş" görünür ve her turda yeniden denenir.
        var sub = await db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == seed.SubscriptionId);
        sub.NextBillingDate.ShouldBe(new DateOnly(2026, 8, 1));
    }

    [Fact]
    public async Task Ayni_kullanim_iki_kez_faturalanmaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Metered, included: 0m, overage: 2m);

        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 10), 100m);

        await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));
        await Billing(tenant).RunAsync(new DateOnly(2026, 8, 1));   // bir sonraki dönem

        await using var db = fixture.CreateContext(tenant);
        var invoices = await db.Invoices.Include(i => i.Lines).AsNoTracking()
            .Where(i => i.SubscriptionId == seed.SubscriptionId)
            .ToListAsync();

        // İkinci turda faturalanacak kullanım kalmadığı için ikinci fatura YOK.
        invoices.Count.ShouldBe(1);
        invoices[0].TaxBaseTotal.ShouldBe(200m);
    }

    [Fact]
    public async Task Gec_gelen_kullanim_bir_sonraki_faturaya_girer()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Metered, included: 0m, overage: 2m);

        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 10), 50m);
        await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));

        // Entegrasyon haziran kaydını temmuzda gönderdi — tarih GEÇMİŞTE.
        // Sorgu tarihe değil DAMGAYA baktığı için bu kayıt kaybolmaz.
        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 28), 30m);
        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 7, 15), 20m);

        await Billing(tenant).RunAsync(new DateOnly(2026, 8, 1));

        await using var db = fixture.CreateContext(tenant);
        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.SubscriptionId == seed.SubscriptionId)
            .OrderBy(i => i.PeriodStart)
            .ToListAsync();

        invoices.Count.ShouldBe(2);
        invoices[0].TaxBaseTotal.ShouldBe(100m);        // 50 × 2
        invoices[1].TaxBaseTotal.ShouldBe(100m);        // (30 + 20) × 2
    }

    [Fact]
    public async Task Ayni_dis_kimlikli_kullanim_iki_kez_kaydedilmez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Metered, included: 0m);

        var first = await Usage(tenant).RecordAsync(
            new UsageEntry(seed.SubscriptionId, 25m, new DateOnly(2026, 6, 10), "SMS", "EVT-1"));

        // Entegrasyon ağ hatası sonrası aynı olayı tekrar gönderdi
        var second = await Usage(tenant).RecordAsync(
            new UsageEntry(seed.SubscriptionId, 25m, new DateOnly(2026, 6, 10), "SMS", "EVT-1"));

        second.ShouldBe(first);

        await using var db = fixture.CreateContext(tenant);
        var total = await db.UsageRecords.AsNoTracking()
            .Where(u => u.SubscriptionId == seed.SubscriptionId)
            .SumAsync(u => u.Quantity);

        total.ShouldBe(25m);            // 50 olsaydı müşteri iki katı öderdi
    }

    [Fact]
    public async Task Sabit_ucretli_plana_kullanim_kaydedilemez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Flat);

        var ex = await Should.ThrowAsync<DomainException>(
            () => AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 10), 10m));

        ex.Message.ShouldContain("kullanım bazlı");
    }

    [Fact]
    public async Task Faturalanmis_kullanim_silinemez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Metered, included: 0m);

        var usageId = await Usage(tenant).RecordAsync(
            new UsageEntry(seed.SubscriptionId, 10m, new DateOnly(2026, 6, 10)));

        await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));

        // Fatura tutarı bu kayda dayanıyor; silinirse fatura dayanaksız kalır.
        await Should.ThrowAsync<DomainException>(() => Usage(tenant).DeleteAsync(usageId));
    }

    [Fact]
    public async Task Ozet_isleyen_donemi_ve_kalan_kotayi_gosterir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Hybrid, included: 100m, overage: 2m);

        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 10), 120m);

        var summary = await Usage(tenant).GetSummaryAsync(seed.SubscriptionId);

        summary.ShouldNotBeNull();
        summary.PeriodStart.ShouldBe(new DateOnly(2026, 6, 1));   // İŞLEYEN dönem
        summary.PeriodEnd.ShouldBe(new DateOnly(2026, 6, 30));
        summary.PeriodQuantity.ShouldBe(120m);
        summary.Allowance.ShouldBe(100m);
        summary.Billable.ShouldBe(20m);
        summary.EstimatedAmount.ShouldBe(40m);
        summary.AllowanceRemaining.ShouldBe(0m);
    }

    [Fact]
    public async Task Onizleme_gercek_turla_ayni_tutari_verir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, BillingModel.Hybrid, included: 100m, overage: 2m);

        await AddUsageAsync(tenant, seed.SubscriptionId, new DateOnly(2026, 6, 10), 300m);

        var preview = await Billing(tenant).PreviewRunAsync(new DateOnly(2026, 7, 1));
        var previewAmount = preview.Billable.Single().Amount;

        preview.Billable.Single().UsageQuantity.ShouldBe(200m);
        preview.Billable.Single().UsageAmount.ShouldBe(400m);

        await Billing(tenant).RunAsync(new DateOnly(2026, 7, 1));

        await using var db = fixture.CreateContext(tenant);
        var invoice = await db.Invoices.AsNoTracking()
                              .FirstAsync(i => i.SubscriptionId == seed.SubscriptionId);

        // ⚠️ Önizleme yalan söylerse kullanıcı onayladığından farklı fatura keser.
        invoice.TaxBaseTotal.ShouldBe(previewAmount);
    }
}
