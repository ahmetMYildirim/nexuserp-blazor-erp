using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Accounting;
using NexusErp.Application.Invoicing;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Invoicing;

/// <summary>
/// Alış faturası satışın AYNASI değil, kardeşi: aynı belge yapısı, ters cari yönü
/// ve numarayı biz değil TEDARİKÇİ veriyor. Burada test edilen üç şey:
/// (1) kendi seri numaramız tüketilmiyor, (2) cari yönü ters, (3) aynı tedarikçiden
/// aynı numara ikinci kez giremiyor.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PurchaseInvoiceTests(DatabaseFixture fixture)
{
    private (InvoiceService Service, AppDbContext Db, Guid SupplierId, Guid CustomerId) Setup(
        Guid tenant)
    {
        // Fatura kesmek otomatik muhasebe fişi üretiyor; fiş hesap planı olmadan
        // yazılamaz. Üretimde her tenant açılışında kuruluyor.
        fixture.SeedChartOfAccounts(tenant);

        var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));
        var service = new InvoiceService(
            fixture.CreateFactory(tenant), generator, TimeProvider.System,
            new AutoPostingService(generator));

        var supplier = new Party
        {
            TenantId = tenant,
            Code = "TED0001",
            Title = "Tedarikçi Lojistik Ltd.",
            Type = PartyType.Supplier,
            PaymentTermDays = 45
        };
        var customer = new Party
        {
            TenantId = tenant,
            Code = "MUS0001",
            Title = "Müşteri A.Ş.",
            Type = PartyType.Customer,
            PaymentTermDays = 30
        };
        db.Parties.AddRange(supplier, customer);
        db.SaveChanges();

        return (service, db, supplier.Id, customer.Id);
    }

    private static InvoiceForm PurchaseForm(Guid partyId, string supplierNo,
                                            decimal price = 1_000m) => new()
    {
        PartyId = partyId,
        Type = InvoiceType.Purchase,
        Series = "ALS",
        SupplierInvoiceNo = supplierNo,
        IssueDate = new DateOnly(2026, 4, 1),
        Lines =
        [
            new InvoiceLineForm
            {
                ProductCode = "NAK",
                ProductName = "Nakliye Hizmeti",
                Unit = "Sefer",
                Quantity = 1m,
                UnitPrice = price,
                TaxRate = 0.20m
            }
        ]
    };

    [Fact]
    public async Task Alis_faturasinda_numara_tedarikciden_gelir_kendi_serimiz_tuketilmez()
    {
        var (service, db, supplierId, customerId) = Setup(Guid.CreateVersion7());
        await using var _ = db;

        var purchaseId = await service.SaveDraftAsync(PurchaseForm(supplierId, "TED-2026-0042"));
        var number = await service.IssueAsync(purchaseId);

        number.ShouldBe("TED-2026-0042");

        var inv = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == purchaseId);
        inv.Status.ShouldBe(InvoiceStatus.Issued);
        inv.Sequence.ShouldBe(0);       // ⚠️ kendi sıramız İLERLEMEDİ

        // Aynı tenant'ta bir satış faturası kesince seri 1'den başlamalı:
        // alış faturası araya girip numara yemişse burası NEX2026000000002 döner.
        var salesId = await service.SaveDraftAsync(new InvoiceForm
        {
            PartyId = customerId,
            Series = "NEX",
            IssueDate = new DateOnly(2026, 4, 2),
            Lines =
            [
                new InvoiceLineForm
                {
                    ProductCode = "HZM", ProductName = "Danışmanlık", Unit = "Saat",
                    Quantity = 1m, UnitPrice = 500m, TaxRate = 0.20m
                }
            ]
        });

        (await service.IssueAsync(salesId)).ShouldBe("NEX2026000000001");
    }

    [Fact]
    public async Task Alis_faturasi_cariyi_alacaklandirir()
    {
        var (service, db, supplierId, _) = Setup(Guid.CreateVersion7());
        await using var _db = db;

        var id = await service.SaveDraftAsync(PurchaseForm(supplierId, "TED-777"));
        await service.IssueAsync(id);

        var entry = await db.PartyLedgerEntries.AsNoTracking()
                            .FirstAsync(e => e.InvoiceId == id);

        // ⚠️ Yön: tedarikçiye BİZ borçlanırız → alacak. Ters yazılırsa cari
        // bakiyesi tedarikçiyi bize borçlu gösterir.
        entry.Type.ShouldBe(LedgerEntryType.PurchaseInvoice);
        entry.Credit.ShouldBe(1_200m);
        entry.Debit.ShouldBe(0m);
        entry.DocumentNumber.ShouldBe("TED-777");
    }

    [Fact]
    public async Task Ayni_tedarikciden_ayni_numara_ikinci_kez_girilemez()
    {
        var (service, db, supplierId, _) = Setup(Guid.CreateVersion7());
        await using var _db = db;

        await service.SaveDraftAsync(PurchaseForm(supplierId, "A-1"));

        // Mükerrer alış faturası hem cariyi hem gideri şişirir; garanti veri tabanında.
        await Should.ThrowAsync<DbUpdateException>(
            () => service.SaveDraftAsync(PurchaseForm(supplierId, "A-1", 2_000m)));
    }

    [Fact]
    public async Task Farkli_tedarikciler_ayni_numarayi_kullanabilir()
    {
        var tenant = Guid.CreateVersion7();
        var (service, db, supplierId, _) = Setup(tenant);
        await using var _db = db;

        var other = new Party
        {
            TenantId = tenant,
            Code = "TED0002",
            Title = "İkinci Tedarikçi",
            Type = PartyType.Supplier
        };
        db.Parties.Add(other);
        await db.SaveChangesAsync();

        // Gerçek hayatta iki firmanın "0001" numaralı faturası olabilir.
        var first = await service.SaveDraftAsync(PurchaseForm(supplierId, "0001"));
        var second = await service.SaveDraftAsync(PurchaseForm(other.Id, "0001"));

        (await service.IssueAsync(first)).ShouldBe("0001");
        (await service.IssueAsync(second)).ShouldBe("0001");
    }

    [Fact]
    public async Task Musteriye_alis_faturasi_kesilemez()
    {
        var (service, db, _, customerId) = Setup(Guid.CreateVersion7());
        await using var _db = db;

        var ex = await Should.ThrowAsync<DomainException>(
            () => service.SaveDraftAsync(PurchaseForm(customerId, "X-1")));

        ex.Message.ShouldContain("tedarikçi");
    }

    [Fact]
    public async Task Tedarikci_numarasi_yoksa_fatura_kaydedilemez()
    {
        var (service, db, supplierId, _) = Setup(Guid.CreateVersion7());
        await using var _db = db;

        var form = PurchaseForm(supplierId, "GECICI");
        var id = await service.SaveDraftAsync(form);

        // Taslak kaydedildikten sonra numara silinirse kesilme aşamasında yakalanmalı
        var draft = await db.Invoices.FirstAsync(i => i.Id == id);
        draft.SupplierInvoiceNo = null;
        await db.SaveChangesAsync();

        var ex = await Should.ThrowAsync<DomainException>(() => service.IssueAsync(id));
        ex.Message.ShouldContain("Tedarikçi fatura numarası");
    }
}
