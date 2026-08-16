using NexusErp.Application.Accounting;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Payments;
using NexusErp.Domain.Accounting;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Accounting;

/// <summary>
/// Mizan, bilanço ve gelir tablosu.
///
/// Bir mali müşavirin sisteme bakınca ilk soracağı soru "mizanı tutuyor mu"
/// olur. Buradaki testler üç eşitliği koruyor:
///   · mizanda borç toplamı = alacak toplamı,
///   · bilançoda aktif = pasif,
///   · gelir tablosunda gelir − gider = dönem sonucu.
/// Bu üçünden biri bozulursa sistem "muhasebe programı" olmaktan çıkar.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class FinancialStatementTests(DatabaseFixture fixture)
    : AccountingTestBase(fixture)
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 12, 31);

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

    /// <summary>Satış 10.000 + KDV, ardından 6.000 tahsilat.</summary>
    private async Task<Seed> BusinessCycleAsync(Guid tenant)
    {
        var seed = await SeedAsync(tenant);

        var invoiceId = await Invoices(tenant).SaveDraftAsync(new InvoiceForm
        {
            PartyId = seed.CustomerId,
            Type = InvoiceType.Sales,
            Series = "NEX",
            IssueDate = new DateOnly(2026, 3, 1),
            Lines =
            [
                new InvoiceLineForm
                {
                    ProductCode = "HZM", ProductName = "Danışmanlık", Unit = "Saat",
                    Quantity = 10m, UnitPrice = 1_000m, TaxRate = 0.20m
                }
            ]
        });
        await Invoices(tenant).IssueAsync(invoiceId);

        await Payments(tenant).CreateAsync(new PaymentForm
        {
            PartyId = seed.CustomerId,
            PaymentDate = new DateOnly(2026, 4, 15),
            Method = PaymentMethod.BankTransfer,
            Amount = 6_000m,
            AutoAllocate = true
        });

        return seed;
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task Mizanda_borc_toplami_alacak_toplamina_esittir()
    {
        var tenant = NewTenant();
        await BusinessCycleAsync(tenant);

        var mizan = await Reports(tenant).GetTrialBalanceAsync(From, To);

        // ⚠️ Mizanın olmazsa olmazı. Tutmuyorsa bir fiş dengesiz kesinleşmiş
        // demektir ve hangisi olduğunu bulmak elle arama gerektirir.
        mizan.IsBalanced.ShouldBeTrue(
            $"borç {mizan.TotalDebit:N2} — alacak {mizan.TotalCredit:N2}");

        // Satış 12.000 (fatura) + 6.000 (tahsilat) = 18.000 her iki tarafta
        mizan.TotalDebit.ShouldBe(18_000m);
        mizan.TotalCredit.ShouldBe(18_000m);
    }

    [Fact]
    public async Task Mizan_hesap_bazinda_dogru_bakiye_verir()
    {
        var tenant = NewTenant();
        await BusinessCycleAsync(tenant);

        var mizan = await Reports(tenant).GetTrialBalanceAsync(From, To);

        // 120 Alıcılar: 12.000 borç (fatura) − 6.000 alacak (tahsilat) = 6.000 borç
        var alicilar = mizan.Rows.Single(r => r.Code == TdhpAccounts.Alicilar);
        alicilar.Debit.ShouldBe(12_000m);
        alicilar.Credit.ShouldBe(6_000m);
        alicilar.Balance.ShouldBe(6_000m);
        alicilar.DebitBalance.ShouldBe(6_000m);

        // 102 Bankalar: 6.000 borç
        mizan.Rows.Single(r => r.Code == TdhpAccounts.Bankalar).Balance.ShouldBe(6_000m);

        // 600 Yurtiçi Satışlar: 10.000 alacak → bakiye negatif (alacak tarafı)
        mizan.Rows.Single(r => r.Code == TdhpAccounts.YurtIciSatislar)
             .CreditBalance.ShouldBe(10_000m);

        // 391 Hesaplanan KDV: 2.000 alacak
        mizan.Rows.Single(r => r.Code == TdhpAccounts.HesaplananKdv)
             .CreditBalance.ShouldBe(2_000m);
    }

    [Fact]
    public async Task Bilancoda_aktif_pasife_esittir()
    {
        var tenant = NewTenant();
        await BusinessCycleAsync(tenant);

        var bilanco = await Reports(tenant).GetBalanceSheetAsync(To);

        // Aktif: 120 Alıcılar 6.000 + 102 Bankalar 6.000 = 12.000
        bilanco.TotalAssets.ShouldBe(12_000m);

        // Pasif: 391 Hesaplanan KDV 2.000 + dönem kârı 10.000 = 12.000
        bilanco.Liabilities.Total.ShouldBe(2_000m);
        bilanco.PeriodResult.ShouldBe(10_000m);

        // ⚠️ Muhasebenin temel denklemi. Tutmuyorsa ya bir fiş dengesiz ya da
        // hesap türü yanlış sınıflandırılmış demektir.
        bilanco.IsBalanced.ShouldBeTrue(
            $"aktif {bilanco.TotalAssets:N2} — pasif {bilanco.TotalLiabilitiesAndEquity:N2}");
    }

    [Fact]
    public async Task Gelir_tablosu_net_kari_dogru_hesaplar()
    {
        var tenant = NewTenant();
        var seed = await SeedAsync(tenant);

        // Satış 10.000 (gelir)
        var salesId = await Invoices(tenant).SaveDraftAsync(new InvoiceForm
        {
            PartyId = seed.CustomerId, Type = InvoiceType.Sales, Series = "NEX",
            IssueDate = new DateOnly(2026, 3, 1),
            Lines =
            [
                new InvoiceLineForm
                {
                    ProductCode = "HZM", ProductName = "Danışmanlık", Unit = "Saat",
                    Quantity = 10m, UnitPrice = 1_000m, TaxRate = 0.20m
                }
            ]
        });
        await Invoices(tenant).IssueAsync(salesId);

        // Alış 4.000 — 153 Ticari Mallar (gider değil VARLIK), gelir tablosuna girmez
        var purchaseId = await Invoices(tenant).SaveDraftAsync(new InvoiceForm
        {
            PartyId = seed.SupplierId, Type = InvoiceType.Purchase, Series = "ALS",
            SupplierInvoiceNo = "TED-77", IssueDate = new DateOnly(2026, 3, 10),
            Lines =
            [
                new InvoiceLineForm
                {
                    ProductCode = "MAL", ProductName = "Ticari Mal", Unit = "Adet",
                    Quantity = 4m, UnitPrice = 1_000m, TaxRate = 0.20m
                }
            ]
        });
        await Invoices(tenant).IssueAsync(purchaseId);

        var gelirTablosu = await Reports(tenant).GetIncomeStatementAsync(From, To);

        gelirTablosu.TotalRevenue.ShouldBe(10_000m);

        // ⚠️ Alış TİCARİ MAL alımıdır, gider değil: stok varlığa yazılır.
        // Gidere yazılsaydı kâr 4.000 eksik görünürdü.
        gelirTablosu.TotalExpense.ShouldBe(0m);
        gelirTablosu.NetResult.ShouldBe(10_000m);
        gelirTablosu.IsProfit.ShouldBeTrue();
    }

    [Fact]
    public async Task Gider_fisi_kari_azaltir()
    {
        var tenant = NewTenant();
        await BusinessCycleAsync(tenant);

        // 770 Genel Yönetim Gideri 3.000 / 100 Kasa 3.000
        var id = await Journals(tenant).SaveDraftAsync(new JournalEntryForm
        {
            EntryDate = new DateOnly(2026, 6, 1),
            Description = "Kira gideri",
            Lines =
            [
                new JournalLineForm
                {
                    AccountId = await AccountIdAsync(tenant, TdhpAccounts.GenelYonetimGideri),
                    Debit = 3_000m
                },
                new JournalLineForm
                {
                    AccountId = await AccountIdAsync(tenant, TdhpAccounts.Kasa),
                    Credit = 3_000m
                }
            ]
        });
        await Journals(tenant).PostAsync(id);

        var gelirTablosu = await Reports(tenant).GetIncomeStatementAsync(From, To);

        gelirTablosu.TotalRevenue.ShouldBe(10_000m);
        gelirTablosu.TotalExpense.ShouldBe(3_000m);
        gelirTablosu.NetResult.ShouldBe(7_000m);

        // Bilanço hâlâ denk olmalı: kasa 3.000 azaldı, dönem kârı 3.000 azaldı.
        var bilanco = await Reports(tenant).GetBalanceSheetAsync(To);
        bilanco.IsBalanced.ShouldBeTrue(
            $"aktif {bilanco.TotalAssets:N2} — pasif {bilanco.TotalLiabilitiesAndEquity:N2}");
    }

    [Fact]
    public async Task Taslak_fis_raporlara_girmez()
    {
        var tenant = NewTenant();
        await BusinessCycleAsync(tenant);

        var oncekiMizan = await Reports(tenant).GetTrialBalanceAsync(From, To);

        // Kesinleştirilmemiş fiş — rapora KARIŞMAMALI.
        await Journals(tenant).SaveDraftAsync(new JournalEntryForm
        {
            EntryDate = new DateOnly(2026, 6, 1),
            Description = "Kesinleşmemiş taslak",
            Lines =
            [
                new JournalLineForm
                {
                    AccountId = await AccountIdAsync(tenant, TdhpAccounts.GenelYonetimGideri),
                    Debit = 50_000m
                },
                new JournalLineForm
                {
                    AccountId = await AccountIdAsync(tenant, TdhpAccounts.Kasa),
                    Credit = 50_000m
                }
            ]
        });

        var sonrakiMizan = await Reports(tenant).GetTrialBalanceAsync(From, To);

        // ⚠️ Taslak dengesiz olabilir; rapora karışsaydı mizan tutmazdı.
        sonrakiMizan.TotalDebit.ShouldBe(oncekiMizan.TotalDebit);
        sonrakiMizan.TotalCredit.ShouldBe(oncekiMizan.TotalCredit);
    }

    [Fact]
    public async Task Tarih_araligi_disindaki_fis_sayilmaz()
    {
        var tenant = NewTenant();
        await BusinessCycleAsync(tenant);

        // Fatura 1 Mart, tahsilat 15 Nisan. Yalnızca mart aralığı sorulursa
        // tahsilat sayılmamalı.
        var mart = await Reports(tenant).GetTrialBalanceAsync(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        mart.TotalDebit.ShouldBe(12_000m);           // yalnızca fatura
        mart.IsBalanced.ShouldBeTrue();
        mart.Rows.ShouldNotContain(r => r.Code == TdhpAccounts.Bankalar);
    }

    [Fact]
    public async Task Baska_firmanin_hareketleri_mizana_karismaz()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await BusinessCycleAsync(tenantA);
        await SeedAsync(tenantB);

        var mizanB = await Reports(tenantB).GetTrialBalanceAsync(From, To);

        // ⚠️ Mizan JournalLines üzerinden gidiyor; tenant filtresi orada
        // unutulmuşsa B firmasının mizanında A firmasının cirosu görünür.
        mizanB.Rows.ShouldBeEmpty();
        mizanB.TotalDebit.ShouldBe(0m);

        var mizanA = await Reports(tenantA).GetTrialBalanceAsync(From, To);
        mizanA.TotalDebit.ShouldBe(18_000m);
    }
}
