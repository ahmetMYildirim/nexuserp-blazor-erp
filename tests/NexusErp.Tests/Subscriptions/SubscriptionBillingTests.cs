using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Subscriptions;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Subscriptions;

[Collection(nameof(DatabaseCollection))]
public sealed class SubscriptionBillingTests(DatabaseFixture fixture)
{
    private sealed record Fixture(
        AppDbContext Db, SubscriptionBillingService Billing, Subscription Sub);

    private Fixture Setup(Guid tenant, DateOnly start, BillingCycle cycle = BillingCycle.Monthly,
                          int? anchorDay = null, decimal price = 1_000m)
    {
        var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));
        var invoiceService = new InvoiceService(db, generator, TimeProvider.System);
        var billing = new SubscriptionBillingService(
            db, invoiceService, NullLogger<SubscriptionBillingService>.Instance);

        var taxRate = new TaxRate
        {
            TenantId = tenant, Code = "KDV20", Name = "KDV %20", Rate = 0.20m,
            ValidFrom = new DateOnly(2023, 7, 10), IsDefault = true
        };
        var product = new Product
        {
            TenantId = tenant, Code = "HZM", Name = "Bakım", Unit = "Ay",
            UnitPrice = price, TaxRateId = taxRate.Id
        };
        var party = new Party
        {
            TenantId = tenant, Code = "MUS0001", Title = "Abone A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var plan = new Plan
        {
            TenantId = tenant, Code = "PRO", Name = "Pro Paket",
            Price = price, Cycle = cycle, ProductId = product.Id
        };
        var sub = new Subscription
        {
            TenantId = tenant, PartyId = party.Id, PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            StartDate = start, NextBillingDate = start,
            BillingAnchorDay = anchorDay ?? start.Day
        };

        db.TaxRates.Add(taxRate);
        db.Products.Add(product);
        db.Parties.Add(party);
        db.Plans.Add(plan);
        db.Subscriptions.Add(sub);
        db.SaveChanges();

        return new Fixture(db, billing, sub);
    }

    /// <summary>
    /// Modülün en kritik iddiası. İş mantığındaki kontrol yarış koşulunda yanılabilir;
    /// garanti (subscription_id, period_start) unique index'inden geliyor.
    /// </summary>
    [Fact]
    public async Task Ayni_donem_icin_ikinci_fatura_uretilmez()
    {
        var f = Setup(Guid.CreateVersion7(), new DateOnly(2026, 3, 1));
        await using var _ = f.Db;

        var first = await f.Billing.RunAsync(new DateOnly(2026, 3, 1));
        var second = await f.Billing.RunAsync(new DateOnly(2026, 3, 1));   // aynı gün tekrar

        first.Created.ShouldBe(1);
        second.Created.ShouldBe(0);      // ✓ idempotent

        (await f.Db.Invoices.CountAsync(i => i.SubscriptionId == f.Sub.Id)).ShouldBe(1);
    }

    /// <summary>
    /// "31 Ocak" problemi. Çapa günü saklanmasaydı 28 Şub → 28 Mar diye kayardı.
    /// </summary>
    [Fact]
    public async Task Uc_ay_boyunca_dogru_tarihlerde_faturalanir()
    {
        var f = Setup(Guid.CreateVersion7(), new DateOnly(2026, 1, 31), anchorDay: 31);
        await using var _ = f.Db;

        await f.Billing.RunAsync(new DateOnly(2026, 1, 31));
        await f.Billing.RunAsync(new DateOnly(2026, 2, 28));
        await f.Billing.RunAsync(new DateOnly(2026, 3, 31));

        var periods = await f.Db.Invoices
            .Where(i => i.SubscriptionId == f.Sub.Id)
            .OrderBy(i => i.PeriodStart)
            .Select(i => i.PeriodStart)
            .ToListAsync();

        periods.ShouldBe([
            new DateOnly(2026, 1, 31),
            new DateOnly(2026, 2, 28),
            new DateOnly(2026, 3, 31)    // ✓ çapa günü geri geldi
        ]);
    }

    [Fact]
    public async Task Uretilen_fatura_kesilmis_ve_donem_bilgisi_dolu()
    {
        var f = Setup(Guid.CreateVersion7(), new DateOnly(2026, 3, 1), price: 4_500m);
        await using var _ = f.Db;

        await f.Billing.RunAsync(new DateOnly(2026, 3, 1));

        var inv = await f.Db.Invoices.Include(i => i.Lines)
                          .FirstAsync(i => i.SubscriptionId == f.Sub.Id);

        inv.Status.ShouldBe(InvoiceStatus.Issued);
        inv.Number.ShouldStartWith("ABN2026");          // abonelik serisi
        inv.PeriodStart.ShouldBe(new DateOnly(2026, 3, 1));
        inv.PeriodEnd.ShouldBe(new DateOnly(2026, 3, 31));
        inv.TaxBaseTotal.ShouldBe(4_500m);
        inv.GrandTotal.ShouldBe(5_400m);                // 4500 + %20 KDV
        inv.Lines.Single().ProductName.ShouldContain("01.03.2026");
    }

    [Fact]
    public async Task Iptal_edilen_abonelik_faturalanmaz()
    {
        var f = Setup(Guid.CreateVersion7(), new DateOnly(2026, 3, 1));
        await using var _ = f.Db;

        f.Sub.Cancel(new DateOnly(2026, 3, 5), immediately: true);
        await f.Db.SaveChangesAsync();

        var result = await f.Billing.RunAsync(new DateOnly(2026, 4, 1));
        result.Created.ShouldBe(0);
    }

    [Fact]
    public async Task Vadesi_gelmemis_abonelik_faturalanmaz()
    {
        var f = Setup(Guid.CreateVersion7(), new DateOnly(2026, 6, 1));
        await using var _ = f.Db;

        var result = await f.Billing.RunAsync(new DateOnly(2026, 3, 1));
        result.Created.ShouldBe(0);
        result.Skipped.ShouldBe(0);
    }

    [Fact]
    public async Task Yillik_abonelik_bir_yil_sonra_yenilenir()
    {
        var f = Setup(Guid.CreateVersion7(), new DateOnly(2026, 3, 15),
                      BillingCycle.Yearly, price: 45_000m);
        await using var _ = f.Db;

        await f.Billing.RunAsync(new DateOnly(2026, 3, 15));

        var sub = await f.Db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == f.Sub.Id);
        sub.NextBillingDate.ShouldBe(new DateOnly(2027, 3, 15));
    }

    [Fact]
    public async Task Gecmis_donemler_tur_tur_yakalanir()
    {
        // 3 ay geriden geliyorsa her turda BİR dönem faturalanır — bilinçli seçim,
        // tek seferde 3 fatura yağmuru operasyonel karar gerektirir.
        var f = Setup(Guid.CreateVersion7(), new DateOnly(2026, 1, 1));
        await using var _ = f.Db;

        var r1 = await f.Billing.RunAsync(new DateOnly(2026, 4, 10));
        var r2 = await f.Billing.RunAsync(new DateOnly(2026, 4, 10));
        var r3 = await f.Billing.RunAsync(new DateOnly(2026, 4, 10));

        r1.Created.ShouldBe(1);
        r2.Created.ShouldBe(1);
        r3.Created.ShouldBe(1);

        (await f.Db.Invoices.CountAsync(i => i.SubscriptionId == f.Sub.Id)).ShouldBe(3);
    }
}
