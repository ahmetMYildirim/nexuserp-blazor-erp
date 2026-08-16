using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Accounting;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Parties;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Invoicing;

[Collection(nameof(DatabaseCollection))]
public sealed class InvoiceServiceTests(DatabaseFixture fixture)
{
    private (InvoiceService Service, AppDbContext Db, Guid PartyId) Setup(Guid tenant)
    {
        fixture.SeedChartOfAccounts(tenant);

        var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));
        var service = new InvoiceService(
            fixture.CreateFactory(tenant), generator, TimeProvider.System,
            new AutoPostingService(generator));

        var party = new Party
        {
            TenantId = tenant,
            Code = "MUS0001",
            Title = "Test Müşteri A.Ş.",
            Type = PartyType.Customer,
            PaymentTermDays = 30
        };
        db.Parties.Add(party);
        db.SaveChanges();

        return (service, db, party.Id);
    }

    private static InvoiceForm Form(Guid partyId, params InvoiceLineForm[] lines) => new()
    {
        PartyId = partyId,
        Series = "NEX",
        IssueDate = new DateOnly(2026, 3, 1),
        Lines = lines.ToList()
    };

    private static InvoiceLineForm Line(decimal qty, decimal price, decimal taxRate = 0.20m,
                                        decimal? withholding = null) => new()
    {
        ProductCode = "HZM",
        ProductName = "Danışmanlık",
        Unit = "Saat",
        Quantity = qty,
        UnitPrice = price,
        TaxRate = taxRate,
        WithholdingRate = withholding
    };

    [Fact]
    public async Task Taslak_kaydedilir_ve_toplamlar_hesaplanir()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var id = await service.SaveDraftAsync(Form(partyId, Line(10m, 100m)));

        var inv = await db.Invoices.Include(i => i.Lines).AsNoTracking()
                          .FirstAsync(i => i.Id == id);

        inv.Status.ShouldBe(InvoiceStatus.Draft);
        inv.Number.ShouldBeNull();               // taslakta numara YOK
        inv.TaxBaseTotal.ShouldBe(1_000m);
        inv.TaxTotal.ShouldBe(200m);
        inv.GrandTotal.ShouldBe(1_200m);
        inv.Lines.Count.ShouldBe(1);
        inv.DueDate.ShouldBe(new DateOnly(2026, 3, 31));   // 1 Mart + 30 gün
    }

    [Fact]
    public async Task Cari_bilgileri_faturaya_kopyalanir()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var id = await service.SaveDraftAsync(Form(partyId, Line(1m, 500m)));

        var inv = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == id);

        // Snapshot: cari unvanı sonradan değişse bile fatura değişmemeli
        inv.PartyTitle.ShouldBe("Test Müşteri A.Ş.");
    }

    [Fact]
    public async Task Kesilince_numara_atanir_ve_durum_degisir()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var id = await service.SaveDraftAsync(Form(partyId, Line(1m, 1_000m)));
        var number = await service.IssueAsync(id);

        number.ShouldBe("NEX2026000000001");

        var inv = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == id);
        inv.Status.ShouldBe(InvoiceStatus.Issued);
        inv.IssuedAt.ShouldNotBeNull();
        inv.AffectsBalance.ShouldBeTrue();
    }

    [Fact]
    public async Task Kesilmis_fatura_duzenlenemez()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var id = await service.SaveDraftAsync(Form(partyId, Line(1m, 1_000m)));
        await service.IssueAsync(id);

        var form = await service.GetFormAsync(id);
        form!.Lines[0].Quantity = 5m;

        var ex = await Should.ThrowAsync<DomainException>(() => service.SaveDraftAsync(form));
        ex.Message.ShouldContain("değiştirilemez");
    }

    [Fact]
    public async Task Kesilmis_fatura_silinemez_ama_iptal_edilebilir()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var id = await service.SaveDraftAsync(Form(partyId, Line(1m, 1_000m)));
        await service.IssueAsync(id);

        var ex = await Should.ThrowAsync<DomainException>(() => service.DeleteDraftAsync(id));
        ex.Message.ShouldContain("Kesilmiş fatura silinemez");

        await service.CancelAsync(id);
        (await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == id))
            .Status.ShouldBe(InvoiceStatus.Cancelled);
    }

    [Fact]
    public async Task Taslak_silinince_soft_delete_olur()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var id = await service.SaveDraftAsync(Form(partyId, Line(1m, 1_000m)));
        await service.DeleteDraftAsync(id);

        (await db.Invoices.CountAsync(i => i.Id == id)).ShouldBe(0);
        (await db.Invoices.IgnoreQueryFilters().CountAsync(i => i.Id == id)).ShouldBe(1);
    }

    [Fact]
    public async Task Tevkifatli_fatura_dogru_hesaplanir()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        // 10.000 TL temizlik, %20 KDV, 7/10 tevkifat
        var id = await service.SaveDraftAsync(
            Form(partyId, Line(1m, 10_000m, 0.20m, withholding: 0.70m)));

        var inv = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == id);

        inv.TaxTotal.ShouldBe(2_000m);
        inv.WithholdingTotal.ShouldBe(1_400m);
        inv.GrandTotal.ShouldBe(10_600m);
    }

    [Fact]
    public async Task Duzenlemede_satirlar_yeniden_yazilir()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var id = await service.SaveDraftAsync(Form(partyId, Line(1m, 1_000m)));

        var form = await service.GetFormAsync(id);
        form!.Lines.Add(Line(2m, 250m));
        await service.SaveDraftAsync(form);

        var inv = await db.Invoices.Include(i => i.Lines).AsNoTracking()
                          .FirstAsync(i => i.Id == id);

        inv.Lines.Count.ShouldBe(2);
        inv.TaxBaseTotal.ShouldBe(1_500m);
        inv.GrandTotal.ShouldBe(1_800m);
    }

    [Fact]
    public async Task Satirsiz_fatura_kaydedilemez()
    {
        var (service, db, partyId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        await Should.ThrowAsync<DomainException>(() => service.SaveDraftAsync(Form(partyId)));
    }

    [Fact]
    public async Task Pasif_cariye_fatura_kesilemez()
    {
        var tenant = Guid.CreateVersion7();
        var (service, db, partyId) = Setup(tenant);
        await using var _ = db;

        var party = await db.Parties.FirstAsync(p => p.Id == partyId);
        party.IsActive = false;
        await db.SaveChangesAsync();

        await Should.ThrowAsync<DomainException>(
            () => service.SaveDraftAsync(Form(partyId, Line(1m, 100m))));
    }
}
