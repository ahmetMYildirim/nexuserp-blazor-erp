using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NexusErp.Application.Events;
using NexusErp.Application.Subscriptions;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Subscriptions;

/// <summary>
/// Dunning: ödenmeyen abonelik faturası → PastDue → 3/7/14 gün hatırlatma →
/// 21 gün askıya alma. Borç kapanırsa geri düzelme.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DunningTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();
    private static readonly DateOnly DueDate = new(2026, 6, 1);

    private sealed record Seed(Guid PartyId, Guid PlanId, Guid SubscriptionId);

    /// <summary>Vadesi 1 Haziran olan, ödenmemiş bir abonelik faturası kurar.</summary>
    private async Task<Seed> SeedAsync(Guid tenant, decimal paid = 0m)
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
            TenantId = tenant, Code = "MUS9201", Title = "Dunning Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var plan = new Plan
        {
            TenantId = tenant, Code = "AYLIK", Name = "Aylık Paket",
            Price = 1_000m, Currency = "TRY", Cycle = BillingCycle.Monthly,
            ProductId = product.Id
        };
        var sub = new Subscription
        {
            TenantId = tenant, PartyId = party.Id, PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            StartDate = DueDate, NextBillingDate = DueDate.AddMonths(1),
            BillingAnchorDay = 1
        };
        var invoice = new Invoice
        {
            TenantId = tenant, Series = "ABN", Year = 2026,
            Number = "ABN2026000000801", Sequence = 801,
            Status = paid > 0 ? InvoiceStatus.PartiallyPaid : InvoiceStatus.Issued,
            Type = InvoiceType.Sales,
            PartyId = party.Id, PartyTitle = party.Title,
            IssueDate = DueDate, DueDate = DueDate,
            Currency = "TRY", GrandTotal = 1_200m, PaidAmount = paid,
            SubscriptionId = sub.Id, PeriodStart = DueDate
        };

        db.TaxRates.Add(taxRate);
        db.Products.Add(product);
        db.Parties.Add(party);
        db.Plans.Add(plan);
        db.Subscriptions.Add(sub);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        return new Seed(party.Id, plan.Id, sub.Id);
    }

    private DunningService Service(Guid tenant) =>
        new(fixture.CreateFactory(tenant), NullLogger<DunningService>.Instance);

    private async Task<Subscription> ReloadAsync(Guid tenant, Guid id)
    {
        await using var db = fixture.CreateContext(tenant);
        return await db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private async Task<List<string>> EventTypesAsync(Guid tenant)
    {
        await using var db = fixture.CreateContext(tenant);
        return await db.OutboxMessages.AsNoTracking()
            .OrderBy(m => m.OccuredAt).Select(m => m.Type).ToListAsync();
    }

    [Fact]
    public async Task Vadesi_gecmemis_fatura_takibe_dusmez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        // Vade günü henüz gelmemiş
        var result = await Service(tenant).RunAsync(DueDate.AddDays(-1));

        result.MarkedPastDue.ShouldBe(0);
        (await ReloadAsync(tenant, seed.SubscriptionId)).Status.ShouldBe(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task Vadesi_gecince_abonelik_pastdue_olur_ve_olay_yayinlanir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var result = await Service(tenant).RunAsync(DueDate.AddDays(1));

        result.MarkedPastDue.ShouldBe(1);

        var sub = await ReloadAsync(tenant, seed.SubscriptionId);
        sub.Status.ShouldBe(SubscriptionStatus.PastDue);
        // ⚠️ Başlangıç turun çalıştığı gün değil, faturanın VADESİ olmalı
        sub.PastDueSince.ShouldBe(DueDate);

        (await EventTypesAsync(tenant)).ShouldContain(nameof(SubscriptionPastDue));
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(7, 2)]
    [InlineData(14, 3)]
    public async Task Esikler_dolunca_hatirlatma_gonderilir(int daysLate, int expectedLevel)
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var result = await Service(tenant).RunAsync(DueDate.AddDays(daysLate));

        result.RemindersSent.ShouldBe(1);
        (await ReloadAsync(tenant, seed.SubscriptionId)).DunningLevel.ShouldBe(expectedLevel);
        (await EventTypesAsync(tenant)).ShouldContain(nameof(SubscriptionPaymentReminder));
    }

    /// <summary>
    /// İşçi günde bir çalışıyor; aynı eşik için ikinci kez hatırlatma
    /// göndermemeli, yoksa müşteri her gün aynı e-postayı alır.
    /// </summary>
    [Fact]
    public async Task Ayni_esik_icin_ikinci_hatirlatma_gonderilmez()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);
        var service = Service(tenant);

        (await service.RunAsync(DueDate.AddDays(3))).RemindersSent.ShouldBe(1);
        (await service.RunAsync(DueDate.AddDays(4))).RemindersSent.ShouldBe(0);
        (await service.RunAsync(DueDate.AddDays(5))).RemindersSent.ShouldBe(0);

        // Bir sonraki eşik dolunca yeniden gönderilir
        (await service.RunAsync(DueDate.AddDays(7))).RemindersSent.ShouldBe(1);
    }

    [Fact]
    public async Task Yirmi_bir_gun_sonra_askiya_alinir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var result = await Service(tenant).RunAsync(DueDate.AddDays(21));

        result.Suspended.ShouldBe(1);

        var sub = await ReloadAsync(tenant, seed.SubscriptionId);
        sub.Status.ShouldBe(SubscriptionStatus.Paused);
        sub.PausedOn.ShouldBe(DueDate.AddDays(21));

        (await EventTypesAsync(tenant)).ShouldContain(nameof(SubscriptionSuspended));
    }

    [Fact]
    public async Task Borc_kapaninca_abonelik_normale_doner()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);
        var service = Service(tenant);

        await service.RunAsync(DueDate.AddDays(5));
        (await ReloadAsync(tenant, seed.SubscriptionId)).Status.ShouldBe(SubscriptionStatus.PastDue);

        // Fatura tahsil edildi
        await using (var db = fixture.CreateContext(tenant))
        {
            var inv = await db.Invoices.FirstAsync(i => i.SubscriptionId == seed.SubscriptionId);
            inv.PaidAmount = inv.GrandTotal;
            inv.RefreshPaymentStatus();
            await db.SaveChangesAsync();
        }

        var result = await service.RunAsync(DueDate.AddDays(6));

        result.Recovered.ShouldBe(1);

        var sub = await ReloadAsync(tenant, seed.SubscriptionId);
        sub.Status.ShouldBe(SubscriptionStatus.Active);
        sub.PastDueSince.ShouldBeNull();
        sub.DunningLevel.ShouldBe(0);

        (await EventTypesAsync(tenant)).ShouldContain(nameof(SubscriptionRecovered));
    }

    [Fact]
    public async Task Kismi_tahsilat_borcu_kapatmaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant, paid: 500m);   // 1.200'ün 500'ü ödendi

        var result = await Service(tenant).RunAsync(DueDate.AddDays(1));

        result.MarkedPastDue.ShouldBe(1);
        (await ReloadAsync(tenant, seed.SubscriptionId)).Status.ShouldBe(SubscriptionStatus.PastDue);
    }

    [Fact]
    public async Task Iptal_edilmis_abonelik_takibe_alinmaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        await using (var db = fixture.CreateContext(tenant))
        {
            var sub = await db.Subscriptions.FirstAsync(s => s.Id == seed.SubscriptionId);
            sub.Status = SubscriptionStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        var result = await Service(tenant).RunAsync(DueDate.AddDays(10));

        result.MarkedPastDue.ShouldBe(0);
        result.Suspended.ShouldBe(0);
    }

    [Fact]
    public async Task Baska_tenantin_abonelikleri_etkilenmez()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var seedA = await SeedAsync(tenantA);
        await SeedAsync(tenantB);

        await Service(tenantA).RunAsync(DueDate.AddDays(5));

        (await ReloadAsync(tenantA, seedA.SubscriptionId)).Status
            .ShouldBe(SubscriptionStatus.PastDue);

        await using var dbB = fixture.CreateContext(tenantB);
        (await dbB.Subscriptions.AsNoTracking().FirstAsync()).Status
            .ShouldBe(SubscriptionStatus.Active);
    }
}
