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

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        // IgnoreQueryFilters: seed anında tenant context henüz kurulu olmayabilir.
        // Uygulama kodunda bu meşru DEĞİL, sistem/seed işlerinde normal (ADR-004).
        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == DemoTenantId, ct))
            return;

        var now = DateTimeOffset.UtcNow;

        var tenant = new Tenant
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
        };
        db.Tenants.Add(tenant);

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
}
