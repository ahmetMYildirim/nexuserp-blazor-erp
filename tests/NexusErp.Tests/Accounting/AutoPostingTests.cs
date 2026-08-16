using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Accounting;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Payments;
using NexusErp.Domain.Accounting;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Accounting;

/// <summary>
/// Fatura ve tahsilattan otomatik muhasebe fişi üretimi.
///
/// Buradaki testlerin hepsi PARA hatası yakalar:
///   · fiş hiç üretilmezse mizan eksik kalır ve o dönemin raporu yanlıştır,
///   · İKİ KEZ üretilirse ciro ve KDV iki katı görünür — bu hata ancak
///     beyanname aşamasında fark edilir, o da geç olur,
///   · yön ters yazılırsa (borç/alacak karışırsa) bilanço ters çıkar.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AutoPostingTests(DatabaseFixture fixture) : AccountingTestBase(fixture)
{
    private InvoiceService Invoices(Guid tenant)
    {
        var generator = Numbers(tenant);
        return new InvoiceService(Fixture.CreateFactory(tenant), generator,
                                  TimeProvider.System, new AutoPostingService(generator));
    }

    private PaymentService Payments(Guid tenant)
    {
        var generator = Numbers(tenant);
        return new PaymentService(Fixture.CreateFactory(tenant), generator,
                                  new AutoPostingService(generator));
    }

    private static InvoiceForm Form(Guid partyId, InvoiceType type, decimal price = 1_000m,
                                    string? supplierNo = null) => new()
    {
        PartyId = partyId,
        Type = type,
        Series = type == InvoiceType.Purchase ? "ALS" : "NEX",
        SupplierInvoiceNo = supplierNo,
        IssueDate = new DateOnly(2026, 5, 1),
        Lines =
        [
            new InvoiceLineForm
            {
                ProductCode = "HZM", ProductName = "Danışmanlık", Unit = "Saat",
                Quantity = 1m, UnitPrice = price, TaxRate = 0.20m
            }
        ]
    };

    private async Task<JournalEntry> EntryForAsync(
        Guid tenant, JournalSourceType type, Guid sourceId)
    {
        await using var db = Fixture.CreateContext(tenant);
        return await db.JournalEntries.Include(j => j.Lines).AsNoTracking()
            .FirstAsync(j => j.SourceType == type && j.SourceId == sourceId);
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task Satis_faturasi_kesilince_fis_uretilir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Invoices(tenant).SaveDraftAsync(Form(seed.CustomerId, InvoiceType.Sales));
        var number = await Invoices(tenant).IssueAsync(id);

        var entry = await EntryForAsync(tenant, JournalSourceType.SalesInvoice, id);

        entry.IsPosted.ShouldBeTrue();
        entry.SourceDocumentNumber.ShouldBe(number);
        entry.Lines.Count.ShouldBe(3);

        // 120 Alıcılar BORÇ 1.200 / 600 Yurtiçi Satışlar ALACAK 1.000
        //                          / 391 Hesaplanan KDV  ALACAK   200
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.Alicilar)
             .Debit.ShouldBe(1_200m);
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.YurtIciSatislar)
             .Credit.ShouldBe(1_000m);
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.HesaplananKdv)
             .Credit.ShouldBe(200m);

        entry.DebitTotal.ShouldBe(entry.CreditTotal);
    }

    [Fact]
    public async Task Alis_faturasi_kaydedilince_fis_uretilir_ve_yon_terstir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Invoices(tenant).SaveDraftAsync(
            Form(seed.SupplierId, InvoiceType.Purchase, supplierNo: "TED-2026-1"));
        await Invoices(tenant).IssueAsync(id);

        var entry = await EntryForAsync(tenant, JournalSourceType.PurchaseInvoice, id);

        // ⚠️ Yön satışın AYNASI: mal ve indirilecek KDV borç, tedarikçi alacak.
        // Ters yazılırsa tedarikçi bize borçlu görünür.
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.TicariMallar)
             .Debit.ShouldBe(1_000m);
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.IndirilecekKdv)
             .Debit.ShouldBe(200m);
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.Saticilar)
             .Credit.ShouldBe(1_200m);

        entry.DebitTotal.ShouldBe(entry.CreditTotal);
    }

    [Fact]
    public async Task Tahsilat_islenince_fis_uretilir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var invoiceId = await Invoices(tenant).SaveDraftAsync(
            Form(seed.CustomerId, InvoiceType.Sales));
        await Invoices(tenant).IssueAsync(invoiceId);

        var paymentId = await Payments(tenant).CreateAsync(new PaymentForm
        {
            PartyId = seed.CustomerId,
            PaymentDate = new DateOnly(2026, 5, 20),
            Method = PaymentMethod.BankTransfer,
            Amount = 1_200m,
            AutoAllocate = true
        });

        var entry = await EntryForAsync(tenant, JournalSourceType.Payment, paymentId);

        // 102 Bankalar BORÇ / 120 Alıcılar ALACAK
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.Bankalar)
             .Debit.ShouldBe(1_200m);
        entry.Lines.Single(l => l.AccountCode == TdhpAccounts.Alicilar)
             .Credit.ShouldBe(1_200m);
    }

    [Fact]
    public async Task Nakit_tahsilat_kasaya_havale_bankaya_yazilir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var cashId = await Payments(tenant).CreateAsync(new PaymentForm
        {
            PartyId = seed.CustomerId, PaymentDate = new DateOnly(2026, 5, 20),
            Method = PaymentMethod.Cash, Amount = 500m
        });

        var entry = await EntryForAsync(tenant, JournalSourceType.Payment, cashId);
        entry.Lines.Single(l => l.Debit > 0).AccountCode.ShouldBe(TdhpAccounts.Kasa);
    }

    [Fact]
    public async Task Ayni_faturadan_ikinci_fis_uretilmez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Invoices(tenant).SaveDraftAsync(Form(seed.CustomerId, InvoiceType.Sales));
        await Invoices(tenant).IssueAsync(id);

        // Servisi doğrudan İKİNCİ kez çağırıyoruz — retry / çift tıklama / kod
        // hatası senaryosu. Fiş zaten varsa null dönmeli, yenisi üretilmemeli.
        await using (var db = Fixture.CreateContext(tenant))
        {
            var invoice = await db.Invoices.FirstAsync(i => i.Id == id);
            var again = await Posting(tenant).BuildForInvoiceAsync(db, invoice);
            again.ShouldBeNull();
            await db.SaveChangesAsync();
        }

        await using var check = Fixture.CreateContext(tenant);
        (await check.JournalEntries.CountAsync(j => j.SourceId == id)).ShouldBe(1);
    }

    [Fact]
    public async Task Ayni_kaynaktan_ikinci_fis_veri_tabaninda_da_engellenir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Invoices(tenant).SaveDraftAsync(Form(seed.CustomerId, InvoiceType.Sales));
        await Invoices(tenant).IssueAsync(id);

        // ⚠️ Servis kontrolünü ATLAYARAK elle ikinci fiş ekliyoruz.
        // (tenant, source_type, source_id) unique index'i son savunma hattı.
        await using var db = Fixture.CreateContext(tenant);
        db.JournalEntries.Add(new JournalEntry
        {
            TenantId = tenant,
            Number = "MUH2026999999999",
            Year = 2026,
            EntryDate = new DateOnly(2026, 5, 1),
            Description = "Mükerrer fiş denemesi",
            SourceType = JournalSourceType.SalesInvoice,
            SourceId = id,
            IsPosted = true,
            DebitTotal = 1_200m,
            CreditTotal = 1_200m
        });

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Proforma_muhasebelesmez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Invoices(tenant).SaveDraftAsync(
            Form(seed.CustomerId, InvoiceType.Proforma));
        await Invoices(tenant).IssueAsync(id);

        // Proforma bağlayıcı olmayan bir tekliftir; cariyi borçlandırmadığı gibi
        // muhasebeleşmez de. Fiş üretilirse olmayan bir satış ciroya yazılır.
        await using var db = Fixture.CreateContext(tenant);
        (await db.JournalEntries.AnyAsync(j => j.SourceId == id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Tahsilat_iptali_ters_kayit_uretir_orijinali_silmez()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var paymentId = await Payments(tenant).CreateAsync(new PaymentForm
        {
            PartyId = seed.CustomerId, PaymentDate = new DateOnly(2026, 5, 20),
            Method = PaymentMethod.BankTransfer, Amount = 800m
        });

        await Payments(tenant).CancelAsync(paymentId);

        var original = await EntryForAsync(tenant, JournalSourceType.Payment, paymentId);
        var reversal = await EntryForAsync(tenant, JournalSourceType.PaymentReversal, paymentId);

        // ⚠️ Orijinal fiş YERİNDE kalmalı: muhasebede kayıt silinmez, ters
        // kayıtla düzeltilir. Denetimde "bu tahsilat neden yok?" sorusuna
        // cevap verebilmek gerekir.
        original.IsPosted.ShouldBeTrue();

        // Ters kayıt yönü çevirir: müşteri borç, banka alacak.
        reversal.Lines.Single(l => l.AccountCode == TdhpAccounts.Alicilar)
                .Debit.ShouldBe(800m);
        reversal.Lines.Single(l => l.AccountCode == TdhpAccounts.Bankalar)
                .Credit.ShouldBe(800m);
    }

    [Fact]
    public async Task Kdvsiz_faturada_kdv_satiri_olusmaz()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var form = Form(seed.CustomerId, InvoiceType.Sales);
        form.Lines[0].TaxRate = 0m;

        var id = await Invoices(tenant).SaveDraftAsync(form);
        await Invoices(tenant).IssueAsync(id);

        var entry = await EntryForAsync(tenant, JournalSourceType.SalesInvoice, id);

        // Sıfır tutarlı satır hem CHECK constraint'e takılır hem muhasebe
        // açısından anlamsızdır.
        entry.Lines.Count.ShouldBe(2);
        entry.Lines.ShouldAllBe(l => l.Debit > 0 || l.Credit > 0);
        entry.DebitTotal.ShouldBe(entry.CreditTotal);
    }

    [Fact]
    public async Task Fatura_ve_fis_ayni_transactionda_yazilir()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        var id = await Invoices(tenant).SaveDraftAsync(Form(seed.CustomerId, InvoiceType.Sales));
        await Invoices(tenant).IssueAsync(id);

        // ⚠️ Fiş outbox üzerinden asenkron üretilseydi burada henüz yazılmamış
        // olurdu. IssueAsync döndüğü anda fişin veri tabanında OLMASI gerekiyor:
        // aksi halde o aralıkta alınan mizan eksik çıkar.
        await using var db = Fixture.CreateContext(tenant);

        var invoiceExists = await db.Invoices.AnyAsync(i => i.Id == id);
        var entryExists = await db.JournalEntries.AnyAsync(j => j.SourceId == id);

        invoiceExists.ShouldBeTrue();
        entryExists.ShouldBeTrue();
    }
}
