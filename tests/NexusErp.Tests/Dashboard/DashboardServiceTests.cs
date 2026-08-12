using NexusErp.Application.Dashboard;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Dashboard;

/// <summary>
/// Dashboard'ın tüm panelleri TEK servis çağrısında üretiliyor. Bu testlerin asıl
/// amacı sorguların EF Core tarafından SQL'e ÇEVRİLEBİLDİĞİNİ kanıtlamak:
/// derleme başarılı olsa bile çevrilemeyen bir LINQ ifadesi ancak çalışma
/// zamanında patlar ve dashboard komple açılmaz.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DashboardServiceTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public async Task Bos_tenantta_tum_paneller_uretilir()
    {
        var tenant = NewTenant();
        var service = new DashboardService(fixture.CreateFactory(tenant));

        var data = await service.GetAsync(Today);

        // Çevrilemeyen bir sorgu olsaydı buraya gelinemezdi
        data.ShouldNotBeNull();
        data.AgingBuckets.Total.ShouldBe(0m);
        data.ProductBreakdown.ShouldBeEmpty();
        data.SubscriptionMovement.NewCount.ShouldBe(0);
        data.DaysSalesOutstanding.ShouldBe(0m);
        data.IssuedThisMonth.ShouldBe(0);
        data.UnallocatedPayments.ShouldBeEmpty();
        data.RevenueTrend.Count.ShouldBe(12);       // eksik aylar dolduruluyor
        data.CollectionTrend.Count.ShouldBe(12);
    }

    [Fact]
    public async Task Kesilen_fatura_yaslandirma_ve_urun_kirilimina_yansir()
    {
        var tenant = NewTenant();
        await using var db = fixture.CreateContext(tenant);

        var party = new Party
        {
            TenantId = tenant, Code = "MUS6001", Title = "Panel Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        db.Parties.Add(party);

        // Vadesi 45 gün geçmiş → 31–60 kovası
        var invoice = new Invoice
        {
            TenantId = tenant, Series = "NEX", Year = Today.Year,
            Number = "NEX" + Today.Year + "000000901", Sequence = 901,
            Status = InvoiceStatus.Issued, Type = InvoiceType.Sales,
            PartyId = party.Id, PartyTitle = party.Title,
            IssueDate = Today, DueDate = Today.AddDays(-45),
            Currency = "TRY", GrandTotal = 1_000m, TaxBaseTotal = 1_000m
        };
        invoice.Lines.Add(new InvoiceLine
        {
            LineNumber = 1,
            ProductCode = "HZM", ProductName = "Danışmanlık", Unit = "Adet",
            Quantity = 1m, UnitPrice = 1_000m, TaxBase = 1_000m, LineTotal = 1_000m
        });
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var data = await new DashboardService(fixture.CreateFactory(tenant)).GetAsync(Today);

        data.AgingBuckets.Days31To60.ShouldBe(1_000m);
        data.AgingBuckets.Total.ShouldBe(1_000m);
        data.IssuedThisMonth.ShouldBe(1);
        data.ProductBreakdown.ShouldContain(p => p.Name == "Danışmanlık" && p.Amount == 1_000m);
    }

    [Fact]
    public async Task Eslesmemis_tahsilat_avans_olarak_listelenir()
    {
        var tenant = NewTenant();
        await using var db = fixture.CreateContext(tenant);

        var party = new Party
        {
            TenantId = tenant, Code = "MUS6002", Title = "Avans Testi Ltd.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        db.Parties.Add(party);
        db.Payments.Add(new Payment
        {
            TenantId = tenant, Number = "THS" + Today.Year + "000000901",
            PartyId = party.Id, PaymentDate = Today, Method = PaymentMethod.BankTransfer,
            Amount = 5_000m, AllocatedAmount = 1_000m, Currency = "TRY",
            Reference = "DEKONT-901"
        });
        await db.SaveChangesAsync();

        var data = await new DashboardService(fixture.CreateFactory(tenant)).GetAsync(Today);

        var row = data.UnallocatedPayments.ShouldHaveSingleItem();
        row.PartyTitle.ShouldBe("Avans Testi Ltd.");
        row.Amount.ShouldBe(4_000m);          // 5.000 − 1.000 eşleşmiş
        row.Reference.ShouldBe("DEKONT-901");
    }

    [Fact]
    public async Task Baska_tenantin_verisi_panellere_sizmaz()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await using (var dbA = fixture.CreateContext(tenantA))
        {
            var party = new Party
            {
                TenantId = tenantA, Code = "MUS6003", Title = "A Tenant",
                Type = PartyType.Customer, PaymentTermDays = 30
            };
            dbA.Parties.Add(party);
            dbA.Payments.Add(new Payment
            {
                TenantId = tenantA, Number = "THS" + Today.Year + "000000902",
                PartyId = party.Id, PaymentDate = Today, Method = PaymentMethod.Cash,
                Amount = 900m, AllocatedAmount = 0m, Currency = "TRY"
            });
            await dbA.SaveChangesAsync();
        }

        var data = await new DashboardService(fixture.CreateFactory(tenantB)).GetAsync(Today);

        data.UnallocatedPayments.ShouldBeEmpty();
        data.AgingBuckets.Total.ShouldBe(0m);
    }
}
