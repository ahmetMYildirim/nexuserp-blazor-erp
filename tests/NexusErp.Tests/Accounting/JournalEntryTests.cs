using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Accounting;
using NexusErp.Domain.Accounting;
using NexusErp.Domain.Common;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Accounting;

/// <summary>
/// Muhasebe fişinin tek değişmezi: SUM(Borç) = SUM(Alacak).
///
/// Buradaki testlerin hepsi aynı felaketi önlüyor: dengesiz bir fişin
/// kesinleşip rapora karışması. Mizan tutmadığında hatanın hangi fişten
/// geldiğini bulmak binlerce kaydı elle taramak demektir; bu yüzden kural
/// hem domain'de hem veri tabanında duruyor ve ikisi de ayrı ayrı test ediliyor.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class JournalEntryTests(DatabaseFixture fixture) : AccountingTestBase(fixture)
{
    private async Task<JournalEntryForm> BalancedFormAsync(Guid tenant, decimal amount = 1_000m)
        => new()
        {
            EntryDate = new DateOnly(2026, 5, 10),
            Description = "Test fişi",
            Lines =
            [
                new JournalLineForm
                {
                    AccountId = await AccountIdAsync(tenant, TdhpAccounts.Kasa),
                    Debit = amount
                },
                new JournalLineForm
                {
                    AccountId = await AccountIdAsync(tenant, TdhpAccounts.YurtIciSatislar),
                    Credit = amount
                }
            ]
        };

    [Fact]
    public async Task Dengeli_fis_kesinlesir_ve_numara_alir()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var id = await Journals(tenant).SaveDraftAsync(await BalancedFormAsync(tenant));
        var number = await Journals(tenant).PostAsync(id);

        number.ShouldStartWith("MUH2026");

        var entry = await Journals(tenant).GetAsync(id);
        entry.ShouldNotBeNull();
        entry.IsPosted.ShouldBeTrue();
        entry.DebitTotal.ShouldBe(1_000m);
        entry.CreditTotal.ShouldBe(1_000m);
    }

    [Fact]
    public async Task Dengesiz_fis_kesinlestirilemez()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var form = await BalancedFormAsync(tenant);
        form.Lines[1].Credit = 900m;                 // 1.000 borç — 900 alacak

        // Taslak olarak KAYDEDİLEBİLİR: kullanıcı satırları girerken zaten dengesizdir.
        var id = await Journals(tenant).SaveDraftAsync(form);

        var ex = await Should.ThrowAsync<DomainException>(() => Journals(tenant).PostAsync(id));
        ex.Message.ShouldContain("dengesiz");
        ex.Message.ShouldContain("100");             // fark kullanıcıya gösterilmeli

        var entry = await Journals(tenant).GetAsync(id);
        entry!.IsPosted.ShouldBeFalse();
    }

    [Fact]
    public async Task Dengesiz_fis_reddedilince_numara_tuketilmez()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var bad = await BalancedFormAsync(tenant);
        bad.Lines[1].Credit = 900m;
        var badId = await Journals(tenant).SaveDraftAsync(bad);

        await Should.ThrowAsync<DomainException>(() => Journals(tenant).PostAsync(badId));

        // ⚠️ Reddedilen deneme numara yakarsa fiş serisinde boşluk kalır ve
        // mevzuat boşluksuz seri ister. İlk BAŞARILI fiş 1 numarayı almalı.
        var goodId = await Journals(tenant).SaveDraftAsync(await BalancedFormAsync(tenant));
        (await Journals(tenant).PostAsync(goodId)).ShouldBe("MUH2026000000001");
    }

    [Fact]
    public async Task Kesinlesmis_fis_degistirilemez_ve_silinemez()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var id = await Journals(tenant).SaveDraftAsync(await BalancedFormAsync(tenant));
        await Journals(tenant).PostAsync(id);

        var edit = await BalancedFormAsync(tenant, 5_000m);
        edit.Id = id;

        (await Should.ThrowAsync<DomainException>(
            () => Journals(tenant).SaveDraftAsync(edit))).Message.ShouldContain("kesinleşmiş");

        (await Should.ThrowAsync<DomainException>(
            () => Journals(tenant).DeleteDraftAsync(id))).Message.ShouldContain("silinemez");

        (await Should.ThrowAsync<DomainException>(
            () => Journals(tenant).PostAsync(id))).Message.ShouldContain("zaten");
    }

    [Fact]
    public async Task Tek_satirli_fis_kaydedilemez()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var form = await BalancedFormAsync(tenant);
        form.Lines.RemoveAt(1);

        // Çift taraflı kaydın tanımı gereği en az iki satır olmalı.
        var ex = await Should.ThrowAsync<DomainException>(
            () => Journals(tenant).SaveDraftAsync(form));
        ex.Message.ShouldContain("en az iki satır");
    }

    [Fact]
    public async Task Bir_satirda_hem_borc_hem_alacak_olamaz()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var form = await BalancedFormAsync(tenant);
        form.Lines[0].Credit = 400m;                 // borç 1.000 + alacak 400

        // Aynı tutar aynı hesabın iki tarafında görünürse bakiye sıfır çıkar
        // ve hareket raporda görünmez olur.
        var ex = await Should.ThrowAsync<DomainException>(
            () => Journals(tenant).SaveDraftAsync(form));
        ex.Message.ShouldContain("tek taraflıdır");
    }

    [Fact]
    public async Task Ara_grup_hesabina_hareket_yazilamaz()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        await using var db = Fixture.CreateContext(tenant);
        var group = await db.Accounts.FirstAsync(a => a.Code == "12");   // Ticari Alacaklar

        var form = await BalancedFormAsync(tenant);
        form.Lines[0].AccountId = group.Id;

        // ⚠️ Ara hesaba hareket yazılırsa mizan tutarı İKİ KEZ toplar:
        // hem hareketin kendisi hem alt hesap toplamı.
        var ex = await Should.ThrowAsync<DomainException>(
            () => Journals(tenant).SaveDraftAsync(form));
        ex.Message.ShouldContain("üst grup");
    }

    [Fact]
    public async Task Veri_tabani_dengesiz_kesinlesmis_fisi_reddeder()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var id = await Journals(tenant).SaveDraftAsync(await BalancedFormAsync(tenant));
        await Journals(tenant).PostAsync(id);

        // ⚠️ Domain kontrolünü ATLAYARAK doğrudan veri tabanına yazıyoruz.
        // Uygulamadan geçmeyen bir yol (elle SQL, toplu içe aktarma) kuralı
        // aşamamalı — CHECK constraint son savunma hattı.
        //
        // Ham SQL EF'in SaveChanges sarmalayıcısından geçmediği için
        // DbUpdateException değil doğrudan sağlayıcının DbException'ı gelir.
        await using var db = Fixture.CreateContext(tenant);

        var ex = await Should.ThrowAsync<DbException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE journal_entries SET credit_total = credit_total + 1 WHERE id = {0}", id));

        ex.Message.ShouldContain("ck_journal_entries_posted_balanced");
    }

    [Fact]
    public async Task Baska_firmanin_fisi_gorunmez()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        await SeedAsync(tenantA);
        await SeedAsync(tenantB);

        var idA = await Journals(tenantA).SaveDraftAsync(await BalancedFormAsync(tenantA));
        await Journals(tenantA).PostAsync(idA);

        // ⚠️ Tenant filtresi bir tek yerde unutulursa burası patlar.
        (await Journals(tenantB).GetAsync(idA)).ShouldBeNull();
        (await Journals(tenantB).SearchAsync()).TotalCount.ShouldBe(0);
        (await Journals(tenantA).SearchAsync()).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task Baska_firmanin_hesap_plani_gorunmez()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        await SeedAsync(tenantA);
        await SeedAsync(tenantB);

        var listA = await Accounts(tenantA).ListAsync();
        var listB = await Accounts(tenantB).ListAsync();

        // Her tenant kendi hesap planına sahip; sayılar eşit ama kimlikler ayrı.
        listA.Count.ShouldBe(listB.Count);
        listA.Select(a => a.Id).Intersect(listB.Select(a => a.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Alt_hesap_acilinca_ust_hesap_hareket_gormez_hale_gelir()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        await Accounts(tenant).CreateAsync(new AccountForm
        {
            Code = "100.01", Name = "Merkez Kasa"
        });

        var list = await Accounts(tenant).ListAsync();

        // 100 artık ara hesap, 100.01 hareket görebilir.
        list.Single(a => a.Code == "100").IsPostable.ShouldBeFalse();
        list.Single(a => a.Code == "100.01").IsPostable.ShouldBeTrue();
    }

    [Fact]
    public async Task Sistem_hesabi_pasiflestirilemez()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);

        var list = await Accounts(tenant).ListAsync();
        var alicilar = list.Single(a => a.Code == TdhpAccounts.Alicilar);

        // Otomatik fişler bu hesaba yazıyor; pasifleşirse fatura kesilemez
        // hale gelir ve sebebi ekranda görünmez.
        var ex = await Should.ThrowAsync<DomainException>(
            () => Accounts(tenant).SetActiveAsync(alicilar.Id, false));
        ex.Message.ShouldContain("sistem hesabı");
    }
}
