using Microsoft.EntityFrameworkCore;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;

namespace NexusErp.Infrastructure.Persistence;

public static class DatabaseSeeder
{
    /// <summary>
    /// Sabit tenant kimliği — her seed'de aynı kalsın diye. Rastgele üretilseydi
    /// migration sıfırlanınca eski verilerle bağ kopardı.
    /// appsettings'teki Tenant:DefaultTenantId ile AYNI olmalı.
    /// </summary>
    public static readonly Guid DemoTenantId = Guid.Parse("0195c8f0-0000-7000-8000-000000000001");

    /// <summary>
    /// Seed BÖLÜM BAZINDA idempotent: yeni modül eklendiğinde mevcut veri tabanına da
    /// uygulanır. Tek bir "tenant var mı?" kontrolüyle erken dönseydi, sonradan eklenen
    /// plan/abonelik verisi hiç oluşmazdı.
    /// </summary>
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await SeedCoreAsync(db, ct);
        await SeedSubscriptionsAsync(db, ct);
        // Fatura ve tahsilat demo verisi Application katmanındaki DemoDataSeeder'da —
        // servisleri kullanması gerekiyor, Infrastructure oraya bağımlı olamaz.
    }

    // ------------------------------------------------------------------
    // Tenant · KDV oranları · cariler · ürünler
    // ------------------------------------------------------------------
    private static async Task SeedCoreAsync(AppDbContext db, CancellationToken ct)
    {
        // IgnoreQueryFilters: seed anında tenant context henüz kurulu olmayabilir.
        // Uygulama kodunda bu meşru DEĞİL, sistem/seed işlerinde normal (ADR-004).
        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == DemoTenantId, ct))
            return;

        var now = DateTimeOffset.UtcNow;

        db.Tenants.Add(new Tenant
        {
            Id = DemoTenantId,
            Name = "Nexus Demo Yazılım A.Ş.",
            TaxNumber = "1234567890",
            TaxOffice = "Kağıthane",
            City = "İstanbul",
            Address = "Merkez Mah. Demo Cad. No:1",
            Email = "muhasebe@nexusdemo.com.tr",
            Phone = "+90 212 000 00 00",
            InvoiceSeries = "NEX",
            CreatedAt = now,
            CreatedBy = "seed"
        });

        // --- KDV oranları (2026) ---
        var rates = new[]
        {
            NewRate("KDV20", "KDV %20", 0.20m, isDefault: true),
            NewRate("KDV10", "KDV %10", 0.10m),
            NewRate("KDV01", "KDV %1",  0.01m),
            NewRate("KDV00", "KDV %0",  0.00m),
        };
        db.TaxRates.AddRange(rates);
        var kdv20 = rates[0];

        // --- Örnek cariler ---
        db.Parties.AddRange(
            NewParty("MUS0001", "Anadolu Lojistik A.Ş.", "1234567890", PartyType.Customer, 30, "İstanbul"),
            NewParty("MUS0002", "Ege Tekstil Ltd. Şti.", "1234567890", PartyType.Customer, 45, "İzmir"),
            NewParty("MUS0003", "Karadeniz İnşaat A.Ş.", "1234567890", PartyType.Customer, 60, "Trabzon"),
            NewParty("TED0001", "Bulut Hosting Ltd.", "1234567890", PartyType.Supplier, 15, "Ankara"),
            NewParty("MUS0004", "Marmara Danışmanlık", "10000000146", PartyType.Both, 30, "İstanbul")
        );

        // --- Örnek ürün / hizmetler ---
        db.Products.AddRange(
            NewProduct("HZM0001", "Yazılım Bakım Hizmeti (Aylık)", 4_500m, "Ay", kdv20),
            NewProduct("HZM0002", "Danışmanlık (Saatlik)", 1_250m, "Saat", kdv20),
            NewProduct("HZM0003", "Temizlik Hizmeti", 8_000m, "Ay", kdv20, withholding: 0.70m),
            NewProduct("URN0001", "Sunucu Lisansı", 24_900m, "Adet", kdv20, ProductKind.Goods)
        );

        await db.SaveChangesAsync(ct);

        // --- yerel yardımcılar ---

        TaxRate NewRate(string code, string name, decimal rate, bool isDefault = false) => new()
        {
            TenantId = DemoTenantId,
            Code = code,
            Name = name,
            Rate = rate,
            IsDefault = isDefault,
            ValidFrom = new DateOnly(2023, 7, 10),   // %18 → %20 geçiş tarihi
            CreatedAt = now,
            CreatedBy = "seed"
        };

        Party NewParty(string code, string title, string taxNo, PartyType type, int term, string city)
        {
            var p = new Party
            {
                TenantId = DemoTenantId,
                Code = code,
                Title = title,
                Type = type,
                PaymentTermDays = term,
                City = city,
                TaxOffice = "Merkez",
                Email = $"{code.ToLowerInvariant()}@ornek.com.tr",
                Phone = "+90 212 111 22 33",
                CreatedAt = now,
                CreatedBy = "seed"
            };
            p.SetTaxNumber(taxNo);
            return p;
        }

        Product NewProduct(string code, string name, decimal price, string unit, TaxRate rate,
                           ProductKind kind = ProductKind.Service, decimal? withholding = null) => new()
        {
            TenantId = DemoTenantId,
            Code = code,
            Name = name,
            UnitPrice = price,
            Unit = unit,
            Kind = kind,
            TaxRateId = rate.Id,
            WithholdingRate = withholding,
            CreatedAt = now,
            CreatedBy = "seed"
        };
    }

    // ------------------------------------------------------------------
    // Abonelik planları ve abonelikler (Bölüm 09)
    // ------------------------------------------------------------------
    private static async Task SeedSubscriptionsAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Plans.IgnoreQueryFilters().AnyAsync(ct)) return;

        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var bakim = await db.Products.IgnoreQueryFilters()
                            .FirstAsync(p => p.Code == "HZM0001", ct);
        var danismanlik = await db.Products.IgnoreQueryFilters()
                            .FirstAsync(p => p.Code == "HZM0002", ct);

        var plans = new[]
        {
            NewPlan("BASIC-AY", "Başlangıç Paketi — Aylık", 1_499m, BillingCycle.Monthly, bakim.Id),
            NewPlan("PRO-AY", "Pro Paket — Aylık", 4_500m, BillingCycle.Monthly, bakim.Id, trialDays: 14),
            NewPlan("PRO-YIL", "Pro Paket — Yıllık", 45_000m, BillingCycle.Yearly, bakim.Id),
            NewPlan("DNS-3AY", "Danışmanlık Paketi — 3 Aylık", 12_000m, BillingCycle.Quarterly, danismanlik.Id),
            // Hibrit: taban ücret + kotayı aşan kullanım
            NewPlan("SMS-AY", "SMS Paketi — Aylık", 750m, BillingCycle.Monthly, bakim.Id,
                    model: BillingModel.Hybrid, unit: "SMS", included: 1_000m, overage: 0.45m),
            // Saf kullanım: sabit ücret YOK, kullanım yoksa fatura da yok
            NewPlan("API-AY", "API Kullanımı — Aylık", 0m, BillingCycle.Monthly, danismanlik.Id,
                    model: BillingModel.Metered, unit: "çağrı", included: 0m, overage: 0.02m),
        };
        db.Plans.AddRange(plans);

        // ⚠️ Yalnızca MÜŞTERİ tipindeki carilere abonelik açılır — tedarikçiye satış
        // faturası kesilemez (Party.EnsureCanBeInvoiced).
        var customers = await db.Parties.IgnoreQueryFilters()
                                .Where(p => (p.Type & PartyType.Customer) != 0)
                                .OrderBy(p => p.Code).ToListAsync(ct);

        // Çapa günü 31 olan abonelik: sonraki fatura tarihi de çapayla hizalı olmalı
        var anchor31Next = new DateOnly(today.Year, today.Month,
                                        Math.Min(31, DateTime.DaysInMonth(today.Year, today.Month)));

        db.Subscriptions.AddRange(
            // Vadesi GELMİŞ — "Şimdi Faturalandır" bunları kesecek
            NewSub(customers[0].Id, plans[1].Id, today.AddMonths(-3), today.AddDays(-2)),
            NewSub(customers[1].Id, plans[0].Id, today.AddMonths(-2), today.AddDays(-1)),
            // Ayın 31'inde başlayan abonelik — çapa günü senaryosu
            NewSub(customers[2].Id, plans[3].Id, new DateOnly(today.Year, 1, 31),
                   anchor31Next, anchorDay: 31),
            // Vadesi GELMEMİŞ
            NewSub(customers[3].Id, plans[2].Id, today.AddMonths(-1), today.AddMonths(11)),
            // Kullanım bazlı senaryolar — vadesi gelmiş, kullanım kayıtları aşağıda
            NewSub(customers[0].Id, plans[4].Id, today.AddMonths(-2), today.AddDays(-1)),
            NewSub(customers[1].Id, plans[5].Id, today.AddMonths(-2), today.AddDays(-1))
        );

        await db.SaveChangesAsync(ct);

        // --- Kullanım kayıtları ---
        // ⚠️ Tarihler GEÇMİŞTE: kullanım ücreti geriye dönük faturalandığı için
        // bugün kaydedilen kullanım bir sonraki dönemde tahsil edilir. Demo verisini
        // bugüne yazsaydık "Faturalandır" butonu boş fatura üretirdi.
        var hybridSub = await db.Subscriptions.IgnoreQueryFilters()
            .Where(x => x.PlanId == plans[4].Id).FirstAsync(ct);
        var meteredSub = await db.Subscriptions.IgnoreQueryFilters()
            .Where(x => x.PlanId == plans[5].Id).FirstAsync(ct);

        db.UsageRecords.AddRange(
            // Hibrit: 1.000 kota, 1.340 kullanım → 340 birim ücretlendirilir
            NewUsage(hybridSub.Id, today.AddDays(-25), 620m, "Toplu SMS gönderimi"),
            NewUsage(hybridSub.Id, today.AddDays(-18), 480m, "Kampanya SMS"),
            NewUsage(hybridSub.Id, today.AddDays(-6), 240m, "Bildirim SMS"),
            // Saf kullanım: kota yok, hepsi ücretlendirilir
            NewUsage(meteredSub.Id, today.AddDays(-20), 18_400m, "API çağrıları (hafta 1)"),
            NewUsage(meteredSub.Id, today.AddDays(-13), 21_150m, "API çağrıları (hafta 2)"),
            NewUsage(meteredSub.Id, today.AddDays(-4), 9_800m, "API çağrıları (hafta 3)")
        );

        await db.SaveChangesAsync(ct);

        // --- yerel yardımcılar ---

        Plan NewPlan(string code, string name, decimal price, BillingCycle cycle,
                     Guid productId, int trialDays = 0,
                     BillingModel model = BillingModel.Flat, string? unit = null,
                     decimal included = 0m, decimal overage = 0m) => new()
        {
            TenantId = DemoTenantId,
            Code = code,
            Name = name,
            Price = price,
            Cycle = cycle,
            TrialDays = trialDays,
            ProductId = productId,
            BillingModel = model,
            UsageUnitName = unit,
            IncludedUnits = included,
            OveragePrice = overage,
            CreatedAt = now,
            CreatedBy = "seed"
        };

        UsageRecord NewUsage(Guid subscriptionId, DateOnly on, decimal quantity,
                             string description) => new()
        {
            TenantId = DemoTenantId,
            SubscriptionId = subscriptionId,
            OccurredOn = on,
            Quantity = quantity,
            Description = description,
            CreatedAt = now,
            CreatedBy = "seed"
        };

        Subscription NewSub(Guid partyId, Guid planId, DateOnly start, DateOnly nextBilling,
                            int? anchorDay = null) => new()
        {
            TenantId = DemoTenantId,
            PartyId = partyId,
            PlanId = planId,
            Status = SubscriptionStatus.Active,
            StartDate = start,
            NextBillingDate = nextBilling,
            BillingAnchorDay = anchorDay ?? start.Day,
            CreatedAt = now,
            CreatedBy = "seed"
        };
    }
}
