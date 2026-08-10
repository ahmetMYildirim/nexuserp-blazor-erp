using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Payments;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Payments;

[Collection(nameof(DatabaseCollection))]
public sealed class PaymentServiceTests(DatabaseFixture fixture)
{
    private sealed record Ctx(
        AppDbContext Db, InvoiceService Invoices, PaymentService Payments,
        PartyBalanceService Balance, Guid PartyId);

    private Ctx Setup(Guid tenant)
    {
        var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));
        var invoices = new InvoiceService(db, generator, TimeProvider.System);
        var payments = new PaymentService(db, generator);
        var balance = new PartyBalanceService(db);

        var party = new Party
        {
            TenantId = tenant, Code = "MUS0001", Title = "Test Müşteri A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        db.Parties.Add(party);
        db.SaveChanges();

        return new Ctx(db, invoices, payments, balance, party.Id);
    }

    private static async Task<Guid> IssueInvoiceAsync(
        Ctx c, decimal amount, DateOnly issueDate, DateOnly dueDate)
    {
        var id = await c.Invoices.SaveDraftAsync(new InvoiceForm
        {
            PartyId = c.PartyId,
            Series = "NEX",
            IssueDate = issueDate,
            DueDate = dueDate,
            Lines =
            [
                new InvoiceLineForm
                {
                    ProductCode = "HZM", ProductName = "Hizmet", Unit = "Adet",
                    Quantity = 1m, UnitPrice = amount, TaxRate = 0m   // KDV yok → net tutar
                }
            ]
        });
        await c.Invoices.IssueAsync(id);
        return id;
    }

    [Fact]
    public async Task Fatura_kesilince_cari_borclanir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        await IssueInvoiceAsync(c, 1_000m, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        (await c.Balance.GetBalanceAsync(c.PartyId)).ShouldBe(1_000m);   // borç
    }

    [Fact]
    public async Task Fifo_en_eski_vadeli_faturadan_baslar()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var i1 = await IssueInvoiceAsync(c, 6_000m, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1));
        var i2 = await IssueInvoiceAsync(c, 4_000m, new DateOnly(2026, 2, 15), new DateOnly(2026, 3, 15));
        var i3 = await IssueInvoiceAsync(c, 9_000m, new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1));

        await c.Payments.CreateAsync(new PaymentForm
        {
            PartyId = c.PartyId, Amount = 15_000m,
            PaymentDate = new DateOnly(2026, 4, 10), AutoAllocate = true
        });

        var inv1 = await c.Db.Invoices.AsNoTracking().FirstAsync(i => i.Id == i1);
        var inv2 = await c.Db.Invoices.AsNoTracking().FirstAsync(i => i.Id == i2);
        var inv3 = await c.Db.Invoices.AsNoTracking().FirstAsync(i => i.Id == i3);

        inv1.PaidAmount.ShouldBe(6_000m);
        inv1.Status.ShouldBe(InvoiceStatus.Paid);
        inv2.PaidAmount.ShouldBe(4_000m);
        inv2.Status.ShouldBe(InvoiceStatus.Paid);
        inv3.PaidAmount.ShouldBe(5_000m);                    // kısmi
        inv3.Status.ShouldBe(InvoiceStatus.PartiallyPaid);

        (await c.Balance.GetBalanceAsync(c.PartyId)).ShouldBe(4_000m);   // 19.000 − 15.000
    }

    [Fact]
    public async Task Fazla_tahsilat_avans_olarak_kalir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        await IssueInvoiceAsync(c, 1_000m, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        var paymentId = await c.Payments.CreateAsync(new PaymentForm
        {
            PartyId = c.PartyId, Amount = 1_500m,
            PaymentDate = new DateOnly(2026, 4, 1), AutoAllocate = true
        });

        var payment = await c.Db.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId);
        payment.AllocatedAmount.ShouldBe(1_000m);
        payment.UnallocatedAmount.ShouldBe(500m);        // avans

        // Biz müşteriye borçluyuz → bakiye ALACAK tarafında (negatif)
        (await c.Balance.GetBalanceAsync(c.PartyId)).ShouldBe(-500m);
    }

    [Fact]
    public async Task Tahsilat_iptali_fatura_durumunu_geri_alir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var invoiceId = await IssueInvoiceAsync(c, 1_000m,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        var paymentId = await c.Payments.CreateAsync(new PaymentForm
        {
            PartyId = c.PartyId, Amount = 1_000m,
            PaymentDate = new DateOnly(2026, 4, 1), AutoAllocate = true
        });

        (await c.Db.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId))
            .Status.ShouldBe(InvoiceStatus.Paid);

        await c.Payments.CancelAsync(paymentId);

        var invoice = await c.Db.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        invoice.PaidAmount.ShouldBe(0m);
        invoice.Status.ShouldBe(InvoiceStatus.Issued);

        // Ters kayıt sayesinde bakiye başlangıç durumuna döner
        (await c.Balance.GetBalanceAsync(c.PartyId)).ShouldBe(1_000m);

        // Tahsilat SİLİNMEZ — iptal işaretlenir, muhasebe izi korunur
        (await c.Db.Payments.CountAsync(p => p.Id == paymentId)).ShouldBe(1);
    }

    [Fact]
    public async Task Fatura_kalan_tutarindan_fazlasi_eslestirilemez()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var invoiceId = await IssueInvoiceAsync(c, 1_000m,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        await Should.ThrowAsync<DomainException>(() => c.Payments.CreateAsync(new PaymentForm
        {
            PartyId = c.PartyId, Amount = 5_000m, AutoAllocate = false,
            PaymentDate = new DateOnly(2026, 4, 1),
            Allocations = [new ManualAllocation(invoiceId, 5_000m)]
        }));
    }

    /// <summary>
    /// Proforma bağlayıcı olmayan bir tekliftir; cariyi BORÇLANDIRMAZ.
    /// Domain'i gerçekten anladığını gösteren test.
    /// </summary>
    [Fact]
    public async Task Proforma_fatura_cari_bakiyeye_islemez()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var id = await c.Invoices.SaveDraftAsync(new InvoiceForm
        {
            PartyId = c.PartyId,
            Type = InvoiceType.Proforma,
            Series = "PRF",
            IssueDate = new DateOnly(2026, 3, 1),
            Lines =
            [
                new InvoiceLineForm
                {
                    ProductCode = "HZM", ProductName = "Teklif", Unit = "Adet",
                    Quantity = 1m, UnitPrice = 5_000m, TaxRate = 0.20m
                }
            ]
        });
        await c.Invoices.IssueAsync(id);

        (await c.Balance.GetBalanceAsync(c.PartyId)).ShouldBe(0m);
    }

    [Fact]
    public async Task Ekstre_devir_satiriyla_baslar_ve_yuruyen_bakiye_dogru()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        await IssueInvoiceAsync(c, 1_000m, new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 10));
        await IssueInvoiceAsync(c, 2_000m, new DateOnly(2026, 3, 5), new DateOnly(2026, 4, 5));
        await c.Payments.CreateAsync(new PaymentForm
        {
            PartyId = c.PartyId, Amount = 500m,
            PaymentDate = new DateOnly(2026, 3, 20), AutoAllocate = true
        });

        var rows = await c.Balance.GetStatementAsync(
            c.PartyId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        rows[0].Description.ShouldBe("Devir");
        rows[0].Balance.ShouldBe(1_000m);          // Ocak faturası devirden geliyor
        rows[^1].Balance.ShouldBe(2_500m);         // 1.000 + 2.000 − 500
    }

    [Fact]
    public async Task Yaslandirma_raporu_kovalari_dogru_doldurur()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var asOf = new DateOnly(2026, 6, 1);

        await IssueInvoiceAsync(c, 1_000m, asOf, asOf.AddDays(10));      // vadesi gelmemiş
        await IssueInvoiceAsync(c, 2_000m, asOf.AddDays(-40), asOf.AddDays(-10));  // 1–30
        await IssueInvoiceAsync(c, 3_000m, asOf.AddDays(-80), asOf.AddDays(-45));  // 31–60
        await IssueInvoiceAsync(c, 4_000m, asOf.AddDays(-140), asOf.AddDays(-100)); // 90+

        var rows = await c.Balance.GetAgingAsync(asOf);
        var row = rows.Single();

        row.NotDue.ShouldBe(1_000m);
        row.Days1To30.ShouldBe(2_000m);
        row.Days31To60.ShouldBe(3_000m);
        row.Over90.ShouldBe(4_000m);
        row.Total.ShouldBe(10_000m);
    }
}
