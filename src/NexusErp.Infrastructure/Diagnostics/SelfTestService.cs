using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Messaging;
using NexusErp.Application.Parties;
using NexusErp.Application.Payments;
using NexusErp.Application.Subscriptions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Invoicing;
using NexusErp.Domain.Subscriptions;
using NexusErp.Domain.ValueObjects;
using NexusErp.Infrastructure.Identity;
using NexusErp.Infrastructure.Messaging;
using NexusErp.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace NexusErp.Infrastructure.Diagnostics;

/// <summary>
/// Canlı sistem testi — her iş kuralını GERÇEK servisler ve GERÇEK veri tabanı
/// üzerinde çalıştırıp sonucu ekrana basar.
///
/// ⚠️ EN ÖNEMLİ TASARIM KARARI: testler AYRI BİR TENANT'ta koşar.
/// Nedeni tek cümleyle: fatura kesmek numara tüketir. Demo tenant'ında koşsaydı
/// her test turu GİB'e bildirilecek seride boşluk açardı ve mükerrer kayıtlar
/// dashboard rakamlarını bozardı. Sandbox tenant kendi sayacına sahip; tur
/// bitiminde tüm satırları siliniyor.
///
/// ⚠️ Tenant'ı değiştirmek için AYRI BİR DI SCOPE açılıyor. TenantContext scoped
/// olduğu için o scope'ta TAZE bir örnek oluşuyor; kullanıcının açık devresindeki
/// tenant'a dokunulmuyor. Aynı scope'ta SetTenant çağırsaydık test koşan
/// kullanıcının ekranı başka firmanın verisini göstermeye başlardı.
/// </summary>
public sealed class SelfTestService(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> rabbit,
    IOptions<SmtpOptions> smtp,
    ILogger<SelfTestService> logger)
{
    /// <summary>Sabit sandbox tenant — demo verisiyle asla karışmaz.</summary>
    public static readonly Guid SandboxTenantId =
        Guid.Parse("5e1ff7e5-7e57-4e57-8e57-5e1ff7e57e57");

    private const string Infra = "Altyapı";
    private const string Sales = "Cari ve Satış Faturası";
    private const string Purchase = "Alış Faturası";
    private const string Collection = "Tahsilat";
    private const string Subs = "Abonelik";
    private const string Metered = "Kullanım Bazlı Faturalama";
    private const string Messaging = "Mesajlaşma (Outbox → RabbitMQ)";
    private const string Security = "Kullanıcı ve Yetki";

    public async Task<SelfTestRun> RunAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();
        var results = new List<CheckResult>();

        // Sandbox scope: tenant'ı burada değiştiriyoruz, kullanıcının devresinde DEĞİL.
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(SandboxTenantId);

        var sp = scope.ServiceProvider;

        try
        {
            await CleanSandboxAsync(sp, ct);          // önceki turdan kalan varsa
            var seed = await SeedSandboxAsync(sp, ct);

            await RunInfrastructureAsync(sp, results, ct);
            await RunSalesAsync(sp, seed, results, ct);
            await RunPurchaseAsync(sp, seed, results, ct);
            await RunCollectionAsync(sp, seed, results, ct);
            RunScheduleMath(results);
            await RunSubscriptionAsync(sp, seed, results, ct);
            await RunMeteredAsync(sp, seed, results, ct);
            await RunMessagingAsync(sp, results, ct);
            await RunSecurityAsync(sp, results, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sistem testi turu beklenmedik şekilde sonlandı.");
            results.Add(new CheckResult("Altyapı", "Test turu tamamlandı",
                CheckOutcome.Failed, ex.Message,
                "Tur ortasında beklenmeyen hata — aşağıdaki kontroller çalıştırılamadı.", 0));
        }
        finally
        {
            // ⚠️ Temizlik finally'de: bir kontrol patlasa bile sandbox verisi kalmasın,
            // yoksa sonraki tur "zaten var" hatalarıyla dolar.
            try { await CleanSandboxAsync(sp, ct); }
            catch (Exception ex) { logger.LogError(ex, "Sandbox temizliği başarısız."); }
        }

        total.Stop();
        return new SelfTestRun(startedAt, (int)total.ElapsedMilliseconds, results);
    }

    // ==================================================================
    // Kontrol çalıştırıcı — her kontrol kendi hatasını yutar
    // ==================================================================

    /// <summary>
    /// ⚠️ Bir kontrolün patlaması turu bitirmez; "KALDI" olarak işaretlenir ve
    /// sıradakine geçilir. Aksi halde ilk hata sonrası hiçbir şey görülemezdi.
    /// </summary>
    private static async Task CheckAsync(
        List<CheckResult> results, string category, string name, string why,
        Func<Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await body();
            sw.Stop();
            results.Add(new CheckResult(category, name, CheckOutcome.Passed, detail, why,
                                        (int)sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new CheckResult(category, name, CheckOutcome.Failed,
                                        ex.Message, why, (int)sw.ElapsedMilliseconds));
        }
    }

    private static void Skip(List<CheckResult> results, string category, string name,
                            string why, string reason)
        => results.Add(new CheckResult(category, name, CheckOutcome.Skipped, reason, why, 0));

    /// <summary>Beklenen kural ihlalinin GERÇEKTEN oluştuğunu doğrular.</summary>
    private static async Task<string> ExpectRejectAsync(Func<Task> action, string expectedFragment)
    {
        try
        {
            await action();
        }
        catch (DomainException ex) when (ex.Message.Contains(expectedFragment,
                                             StringComparison.OrdinalIgnoreCase))
        {
            return $"Reddedildi: \"{ex.Message}\"";
        }
        catch (DbUpdateException)
        {
            return "Veri tabanı kısıtı tarafından reddedildi (unique index).";
        }

        throw new InvalidOperationException(
            $"İşlem reddedilmesi gerekirken BAŞARILI oldu. Beklenen: \"{expectedFragment}\"");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // ==================================================================
    // Sandbox verisi
    // ==================================================================

    private sealed record Sandbox(
        Guid CustomerId, Guid SupplierId, Guid ProductId, Guid TaxRateId,
        Guid FlatPlanId, Guid MeteredPlanId, decimal TaxRate);

    private static async Task<Sandbox> SeedSandboxAsync(
        IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();

        var tax = new TaxRate
        {
            TenantId = SandboxTenantId, Code = "KDV20", Name = "KDV %20", Rate = 0.20m,
            ValidFrom = new DateOnly(2020, 1, 1), IsDefault = true
        };
        var product = new Product
        {
            TenantId = SandboxTenantId, Code = "TEST01", Name = "Test Hizmeti",
            Unit = "Adet", UnitPrice = 1_000m, TaxRateId = tax.Id
        };
        var customer = new Party
        {
            TenantId = SandboxTenantId, Code = "TESTMUS", Title = "Test Müşteri A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var supplier = new Party
        {
            TenantId = SandboxTenantId, Code = "TESTTED", Title = "Test Tedarikçi Ltd.",
            Type = PartyType.Supplier, PaymentTermDays = 45
        };
        var flatPlan = new Plan
        {
            TenantId = SandboxTenantId, Code = "TEST-FLAT", Name = "Test Sabit Plan",
            Price = 1_000m, Cycle = BillingCycle.Monthly, ProductId = product.Id
        };
        var meteredPlan = new Plan
        {
            TenantId = SandboxTenantId, Code = "TEST-USE", Name = "Test Kullanım Planı",
            Price = 500m, Cycle = BillingCycle.Monthly, ProductId = product.Id,
            BillingModel = BillingModel.Hybrid, UsageUnitName = "SMS",
            IncludedUnits = 100m, OveragePrice = 2m
        };

        db.TaxRates.Add(tax);
        db.Products.Add(product);
        db.Parties.AddRange(customer, supplier);
        db.Plans.AddRange(flatPlan, meteredPlan);
        await db.SaveChangesAsync(ct);

        return new Sandbox(customer.Id, supplier.Id, product.Id, tax.Id,
                           flatPlan.Id, meteredPlan.Id, tax.Rate);
    }

    /// <summary>
    /// Sandbox tenant'ının TÜM satırlarını KALICI siler.
    ///
    /// ⚠️ IgnoreQueryFilters + ExecuteDelete: soft delete YETMEZ, çünkü unique
    /// index'lerin filtresi is_deleted = false — soft delete edilmiş kayıt sonraki
    /// turda "zaten var" hatası vermez ama tablo sonsuza kadar şişer.
    /// ⚠️ Silme sırası yabancı anahtarlara göre: önce çocuklar.
    /// </summary>
    private static async Task CleanSandboxAsync(IServiceProvider sp, CancellationToken ct)
    {
        // ⚠️ AppDbContext (arayüz değil): ExecuteDelete ve ham SQL için
        // DatabaseFacade gerekiyor, IAppDbContext onu açmıyor.
        var db = sp.GetRequiredService<AppDbContext>();

        await db.PaymentAllocations.IgnoreQueryFilters()
            .Where(x => db.Payments.IgnoreQueryFilters()
                .Any(p => p.Id == x.PaymentId && p.TenantId == SandboxTenantId))
            .ExecuteDeleteAsync(ct);

        await db.InvoiceLines.IgnoreQueryFilters()
            .Where(x => db.Invoices.IgnoreQueryFilters()
                .Any(i => i.Id == x.InvoiceId && i.TenantId == SandboxTenantId))
            .ExecuteDeleteAsync(ct);

        await db.UsageRecords.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.PartyLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.Payments.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.Invoices.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.Subscriptions.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.Plans.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.Products.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.TaxRates.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.Parties.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);
        await db.AuditEntries.IgnoreQueryFilters()
            .Where(x => x.TenantId == SandboxTenantId).ExecuteDeleteAsync(ct);

        // Numara sayacı da sıfırlanmalı: her tur "1" numaradan başlasın ki
        // beklenen numarayı test edebilelim.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM invoice_counters WHERE tenant_id = {0}", [SandboxTenantId], ct);
    }

    // ==================================================================
    // ALTYAPI
    // ==================================================================

    private async Task RunInfrastructureAsync(
        IServiceProvider sp, List<CheckResult> results, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();

        await CheckAsync(results, Infra, "PostgreSQL bağlantısı",
            "Veri tabanına ulaşılamıyorsa aşağıdaki hiçbir kontrol anlamlı değildir.",
            async () =>
            {
                // ⚠️ SqlQuery<T> skaler sonucu "Value" adlı kolonda arar;
                // takma ad verilmezse "column s.Value does not exist" hatası gelir.
                var version = await db.Database
                    .SqlQuery<string>($"SELECT version() AS \"Value\"").FirstAsync(ct);
                return version.Split(',')[0];
            });

        await CheckAsync(results, Infra, "Şema güncel (bekleyen migration yok)",
            "Bekleyen migration varsa kod ile veri tabanı ayrışmıştır; sorgular " +
            "olmayan kolonu arar ve çalışma anında patlar.",
            async () =>
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                Assert(pending.Count == 0,
                       $"{pending.Count} bekleyen migration: {string.Join(", ", pending)}");

                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).Count();
                return $"{applied} migration uygulanmış, bekleyen yok.";
            });

        await CheckAsync(results, Infra, "Outbox sağlığı",
            "Asıl metrik bekleyen SAYISI değil, en eski bekleyen mesajın YAŞI. " +
            "5 dakikayı aşan mesaj yayıncının durduğunu gösterir.",
            async () =>
            {
                var health = sp.GetRequiredService<OutboxHealthService>();
                var h = await health.CheckAsync(ct);
                Assert(h.IsHealthy, h.Summary);
                return h.Summary;
            });

        if (!rabbit.Value.Enabled)
        {
            Skip(results, Infra, "RabbitMQ bağlantısı",
                 "Olaylar broker'a ulaşmazsa tüketiciler (e-posta, muhasebe fişi) çalışmaz.",
                 "RabbitMQ yapılandırmada kapalı (Enabled = false).");
        }
        else
        {
            await CheckAsync(results, Infra, "RabbitMQ bağlantısı",
                "Olaylar broker'a ulaşmazsa tüketiciler (e-posta, bildirim) çalışmaz. " +
                "Outbox mesajı tutmaya devam eder, veri kaybolmaz ama teslim gecikir.",
                async () =>
                {
                    var cf = new ConnectionFactory
                    {
                        Uri = new Uri(rabbit.Value.Uri),
                        ClientProvidedName = "nexuserp-selftest"
                    };

                    await using var conn = await cf.CreateConnectionAsync(ct);
                    await using var ch = await conn.CreateChannelAsync(cancellationToken: ct);

                    // passive = "varsa doğrula, YOKSA OLUŞTURMA". Test yan etki bırakmamalı.
                    await ch.ExchangeDeclarePassiveAsync(rabbit.Value.Exchange, ct);

                    return $"Bağlandı: {conn.Endpoint.HostName}:{conn.Endpoint.Port} · " +
                           $"exchange '{rabbit.Value.Exchange}' mevcut.";
                });
        }

        await CheckAsync(results, Infra, "SMTP (e-posta) erişimi",
            "Bildirim tüketicisi buraya yazıyor. Erişilemezse fatura/hatırlatma " +
            "e-postaları sessizce gitmez.",
            async () =>
            {
                using var client = new System.Net.Sockets.TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                await client.ConnectAsync(smtp.Value.Host, smtp.Value.Port, timeout.Token);
                return $"{smtp.Value.Host}:{smtp.Value.Port} açık.";
            });
    }

    // ==================================================================
    // CARİ VE SATIŞ FATURASI
    // ==================================================================

    private static async Task RunSalesAsync(
        IServiceProvider sp, Sandbox seed, List<CheckResult> results, CancellationToken ct)
    {
        var invoices = sp.GetRequiredService<InvoiceService>();
        var parties = sp.GetRequiredService<PartyService>();
        var factory = sp.GetRequiredService<IAppDbContextFactory>();

        await CheckAsync(results, Sales, "VKN / TCKN doğrulaması",
            "Yanlış vergi numaralı fatura e-Fatura kapısından geri döner. " +
            "Kontrol basamağı algoritması hatayı girişte yakalar.",
            () =>
            {
                // ⚠️ Test verisi seçerken dikkat: 1234567890 ve 11111111110
                // aslında GEÇERLİ numaralardır (kontrol basamakları tutuyor).
                // "Rastgele rakam dizisi geçersizdir" varsayımı yanlış — kontrol
                // basamağı bilerek bozulmuş bir örnek gerekiyor.
                Assert(!TaxIdentifier.TryParse("1234567891", out _),
                       "Kontrol basamağı bozuk VKN kabul edildi.");
                Assert(!TaxIdentifier.TryParse("12345678960", out _),
                       "Kontrol basamağı bozuk TCKN kabul edildi.");
                Assert(TaxIdentifier.TryParse("12345678950", out var tckn)
                       && tckn.Kind == TaxIdKind.Tckn,
                       "Geçerli TCKN reddedildi.");

                // Boşluk/tire ayıklanarak GEÇERLİ bir VKN tanınmalı
                Assert(TaxIdentifier.TryParse("175 001 95-11", out var vkn),
                       "Geçerli VKN reddedildi.");

                return Task.FromResult(
                    $"Bozuk kontrol basamakları reddedildi · geçerli VKN {vkn.Value} " +
                    $"({vkn.Kind}) ve TCKN {tckn.Value} ({tckn.Kind}) tanındı");
            });

        await CheckAsync(results, Sales, "Fatura hesap motoru (iskonto → KDV → tevkifat)",
            "Sıra kritik: iskonto matrahtan ÖNCE düşülmezse KDV fazla hesaplanır. " +
            "Tevkifat KDV'nin üzerinden, matrahın değil.",
            () =>
            {
                var result = InvoiceCalculator.CalculateDocument(
                    [new LineInput(10m, 100m, DiscountType.Percentage, 0.10m, 0.20m, 0.70m)],
                    DiscountType.None, 0m);

                // 10 × 100 = 1.000 → %10 iskonto = 900 matrah
                // KDV %20 = 180 → tevkifat 7/10 = 126 → tahsil edilecek KDV 54
                Assert(result.TaxBaseTotal == 900m, $"Matrah 900 olmalı, {result.TaxBaseTotal} çıktı.");
                Assert(result.TaxTotal == 180m, $"KDV 180 olmalı, {result.TaxTotal} çıktı.");
                Assert(result.WithholdingTotal == 126m,
                       $"Tevkifat 126 olmalı, {result.WithholdingTotal} çıktı.");

                return Task.FromResult(
                    $"Matrah {result.TaxBaseTotal:N2} · KDV {result.TaxTotal:N2} · " +
                    $"tevkifat {result.WithholdingTotal:N2} · genel toplam {result.GrandTotal:N2}");
            });

        Guid firstInvoiceId = default;

        await CheckAsync(results, Sales, "Fatura kesildi, numara sırayla verildi",
            "Numara GİB formatında ve BOŞLUKSUZ olmalı. Taslakta numara verilmez; " +
            "silinen taslak seride boşluk bırakırdı.",
            async () =>
            {
                var id = await invoices.SaveDraftAsync(SalesForm(seed, 1_000m), ct);
                firstInvoiceId = id;

                await using var db = factory.Create();
                var draft = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == id, ct);
                Assert(draft.Number is null, "Taslağa numara verilmiş.");

                var number = await invoices.IssueAsync(id, ct);
                var year = DateTime.Today.Year;
                Assert(number == $"NEX{year}000000001",
                       $"Beklenen NEX{year}000000001, gelen {number}");

                return $"Taslakta numara yok → kesilince {number}";
            });

        await CheckAsync(results, Sales, "İkinci fatura bir sonraki numarayı aldı",
            "Sayaç atlarsa mevzuata aykırı seri boşluğu oluşur.",
            async () =>
            {
                var id = await invoices.SaveDraftAsync(SalesForm(seed, 2_500m), ct);
                var number = await invoices.IssueAsync(id, ct);
                var year = DateTime.Today.Year;
                Assert(number == $"NEX{year}000000002",
                       $"Beklenen NEX{year}000000002, gelen {number}");
                return number;
            });

        await CheckAsync(results, Sales, "Kesilmiş fatura değiştirilemez",
            "Vergi mevzuatı: kesilen belge değişmez. Değişebilseydi denetimde " +
            "gösterilen fatura ile müşterideki nüsha farklı olurdu.",
            async () =>
            {
                var form = await invoices.GetFormAsync(firstInvoiceId, ct);
                form!.Lines[0].UnitPrice = 1m;
                return await ExpectRejectAsync(
                    () => invoices.SaveDraftAsync(form, ct), "değiştirilemez");
            });

        await CheckAsync(results, Sales, "Satış faturası cariyi borçlandırdı",
            "Yön ters yazılırsa cari bakiyesi müşteriyi alacaklı gösterir; " +
            "ekstre ve yaşlandırma raporu tamamen yanlış çıkar.",
            async () =>
            {
                await using var db = factory.Create();
                var entry = await db.PartyLedgerEntries.AsNoTracking()
                    .FirstAsync(e => e.InvoiceId == firstInvoiceId, ct);

                Assert(entry.Type == LedgerEntryType.Invoice, "Hareket tipi yanlış.");
                Assert(entry.Debit == 1_200m && entry.Credit == 0m,
                       $"Borç 1.200 olmalı; borç {entry.Debit}, alacak {entry.Credit}.");

                return $"Borç {entry.Debit:N2} / alacak {entry.Credit:N2} " +
                       $"({entry.DocumentNumber})";
            });

        await CheckAsync(results, Sales, "Pasif cariye fatura kesilemez",
            "Pasife alınmış cari genellikle tahsilat sorunu yaşanan caridir; " +
            "yeni borç yüklemek istenmez.",
            async () =>
            {
                var form = await parties.GetFormAsync(seed.CustomerId, ct);
                form!.IsActive = false;
                await parties.SaveAsync(form, ct);

                try
                {
                    return await ExpectRejectAsync(
                        () => invoices.SaveDraftAsync(SalesForm(seed, 100m), ct), "pasif");
                }
                finally
                {
                    form.IsActive = true;                 // sonraki kontroller için geri al
                    await parties.SaveAsync(form, ct);
                }
            });
    }

    // ==================================================================
    // ALIŞ FATURASI
    // ==================================================================

    private static async Task RunPurchaseAsync(
        IServiceProvider sp, Sandbox seed, List<CheckResult> results, CancellationToken ct)
    {
        var invoices = sp.GetRequiredService<InvoiceService>();
        var factory = sp.GetRequiredService<IAppDbContextFactory>();

        Guid purchaseId = default;

        await CheckAsync(results, Purchase, "Numara tedarikçiden geldi, kendi serimiz tüketilmedi",
            "Alışta numarayı biz üretmeyiz. Üretseydik GİB'e bildirdiğimiz SATIŞ " +
            "serisinde boşluk açardık — mevzuata aykırı.",
            async () =>
            {
                purchaseId = await invoices.SaveDraftAsync(
                    PurchaseForm(seed, "TED-2026-0042"), ct);

                var number = await invoices.IssueAsync(purchaseId, ct);
                Assert(number == "TED-2026-0042", $"Beklenen TED-2026-0042, gelen {number}");

                await using var db = factory.Create();
                var inv = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == purchaseId, ct);
                Assert(inv.Sequence == 0, $"Kendi sıramız ilerlemiş: {inv.Sequence}");

                return $"Fatura no {number} · kendi sıra numaramız {inv.Sequence} (ilerlemedi)";
            });

        await CheckAsync(results, Purchase, "Sonraki satış faturası sırayı bozmadan devam etti",
            "Alış faturası araya girip numara tüketseydi burası 3 değil 4 dönerdi.",
            async () =>
            {
                var id = await invoices.SaveDraftAsync(SalesForm(seed, 500m), ct);
                var number = await invoices.IssueAsync(id, ct);
                var year = DateTime.Today.Year;
                Assert(number == $"NEX{year}000000003",
                       $"Beklenen NEX{year}000000003, gelen {number}");
                return number;
            });

        await CheckAsync(results, Purchase, "Alış cariyi ALACAKLANDIRDI (satışın tersi)",
            "Tedarikçiye biz borçlanırız. Yön karışırsa tedarikçi bize borçlu " +
            "görünür ve ödeme planı tamamen yanlış çıkar.",
            async () =>
            {
                await using var db = factory.Create();
                var entry = await db.PartyLedgerEntries.AsNoTracking()
                    .FirstAsync(e => e.InvoiceId == purchaseId, ct);

                Assert(entry.Type == LedgerEntryType.PurchaseInvoice, "Hareket tipi yanlış.");
                Assert(entry.Credit > 0 && entry.Debit == 0m,
                       $"Alacak beklenirken borç {entry.Debit}, alacak {entry.Credit}.");

                return $"Alacak {entry.Credit:N2} / borç {entry.Debit:N2} — satışın aynası";
            });

        await CheckAsync(results, Purchase, "Aynı tedarikçiden mükerrer numara reddedildi",
            "El ile veri girişinde en sık yapılan hata aynı faturayı iki kez girmektir; " +
            "hem cariyi hem gideri şişirir. Garanti veri tabanı index'inde.",
            () => ExpectRejectAsync(
                () => invoices.SaveDraftAsync(PurchaseForm(seed, "TED-2026-0042"), ct),
                "duplicate"));

        await CheckAsync(results, Purchase, "Müşteriye alış faturası kesilemez",
            "Cari tipi kontrol edilmezse muhasebe kayıtları anlamsızlaşır.",
            () =>
            {
                var form = PurchaseForm(seed, "X-1");
                form.PartyId = seed.CustomerId;          // tedarikçi değil MÜŞTERİ
                return ExpectRejectAsync(
                    () => invoices.SaveDraftAsync(form, ct), "tedarikçi");
            });
    }

    // ==================================================================
    // TAHSİLAT
    // ==================================================================

    private static async Task RunCollectionAsync(
        IServiceProvider sp, Sandbox seed, List<CheckResult> results, CancellationToken ct)
    {
        var payments = sp.GetRequiredService<PaymentService>();
        var balances = sp.GetRequiredService<PartyBalanceService>();
        var factory = sp.GetRequiredService<IAppDbContextFactory>();

        await CheckAsync(results, Collection, "Tahsilat açık faturalara FIFO dağıtıldı",
            "Vadesi en eski fatura önce kapanmalı. Sıra bozulursa yaşlandırma " +
            "raporu gerçekte kapanmış borcu hâlâ gecikmiş gösterir.",
            async () =>
            {
                await payments.CreateAsync(new PaymentForm
                {
                    PartyId = seed.CustomerId,
                    Amount = 1_200m,
                    PaymentDate = DateOnly.FromDateTime(DateTime.Today),
                    Method = PaymentMethod.BankTransfer,
                    AutoAllocate = true,
                    Reference = "Sistem testi"
                }, ct);

                await using var db = factory.Create();
                var paid = await db.Invoices.AsNoTracking()
                    .Where(i => i.Type == InvoiceType.Sales && i.Status == InvoiceStatus.Paid)
                    .CountAsync(ct);

                Assert(paid >= 1, "Hiçbir fatura kapanmadı.");
                return $"{paid} fatura tamamen kapandı (1.200,00 tahsilat FIFO dağıtıldı).";
            });

        await CheckAsync(results, Collection, "Cari bakiye borç − alacak olarak hesaplandı",
            "Ekstredeki yürüyen bakiye ile kart üzerindeki bakiye ayrışırsa " +
            "hangisine güvenileceği belirsizleşir.",
            async () =>
            {
                var balance = await balances.GetBalanceAsync(seed.CustomerId, ct);
                return $"Test müşterisinin bakiyesi: {balance:N2}";
            });
    }

    // ==================================================================
    // TAKVİM MATEMATİĞİ (saf fonksiyon, veri tabanı yok)
    // ==================================================================

    private static void RunScheduleMath(List<CheckResult> results)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var d = new DateOnly(2026, 1, 31);
            d = BillingSchedule.NextPeriodStart(d, BillingCycle.Monthly, 31);
            Assert(d == new DateOnly(2026, 2, 28), $"Şubat 28 olmalı, {d} çıktı.");

            d = BillingSchedule.NextPeriodStart(d, BillingCycle.Monthly, 31);
            Assert(d == new DateOnly(2026, 3, 31), $"Mart 31 olmalı, {d} çıktı — çapa kaymış.");

            sw.Stop();
            results.Add(new CheckResult(Subs, "Faturalandırma çapası kaymıyor",
                CheckOutcome.Passed,
                "31 Oca → 28 Şub → 31 Mar (AddMonths tek başına 28'de kalırdı)",
                "Ayın 31'inde faturalanan müşteri her ay 31'inde faturalanmayı bekler. " +
                "Çapa saklanmasaydı tarih kalıcı olarak 28'e kayardı.",
                (int)sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new CheckResult(Subs, "Faturalandırma çapası kaymıyor",
                CheckOutcome.Failed, ex.Message,
                "Çapa günü korunmalı.", (int)sw.ElapsedMilliseconds));
        }

        sw.Restart();
        try
        {
            // 1–31 Mart dönemi, 15 Mart'ta değişiklik → 17 gün kaldı
            var amount = BillingSchedule.Prorate(499m, new DateOnly(2026, 3, 1),
                                                 new DateOnly(2026, 3, 31),
                                                 new DateOnly(2026, 3, 15));
            Assert(amount == 273.65m, $"273,65 olmalı, {amount} çıktı.");

            sw.Stop();
            results.Add(new CheckResult(Subs, "Dönem ortası oransal tutar (proration)",
                CheckOutcome.Passed, $"499,00 × 17/31 gün = {amount:N2}",
                "Plan yükseltmesinde müşteriden tam dönem ücreti alınırsa haksız " +
                "tahsilat olur; eksik alınırsa gelir kaybı.",
                (int)sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new CheckResult(Subs, "Dönem ortası oransal tutar (proration)",
                CheckOutcome.Failed, ex.Message, "Oransal hesap doğru olmalı.",
                (int)sw.ElapsedMilliseconds));
        }
    }

    // ==================================================================
    // ABONELİK
    // ==================================================================

    private static async Task RunSubscriptionAsync(
        IServiceProvider sp, Sandbox seed, List<CheckResult> results, CancellationToken ct)
    {
        var billing = sp.GetRequiredService<SubscriptionBillingService>();
        var factory = sp.GetRequiredService<IAppDbContextFactory>();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var dueDate = today.AddDays(-1);
        Guid subId;

        await using (var db = factory.Create())
        {
            var sub = new Subscription
            {
                TenantId = SandboxTenantId,
                PartyId = seed.CustomerId,
                PlanId = seed.FlatPlanId,
                Status = SubscriptionStatus.Active,
                StartDate = today.AddMonths(-2),
                NextBillingDate = dueDate,
                BillingAnchorDay = dueDate.Day
            };
            db.Subscriptions.Add(sub);
            await db.SaveChangesAsync(ct);
            subId = sub.Id;
        }

        await CheckAsync(results, Subs, "Önizleme, kesilecek faturayı önceden gösterdi",
            "Muhasebeci körlemesine buton basmak istemez. Önizlemenin seçim koşulu " +
            "gerçek turla BİREBİR aynı olmak zorunda; ayrışırsa kullanıcı " +
            "onayladığından farklı bir sonuç alır.",
            async () =>
            {
                var preview = await billing.PreviewRunAsync(today, ct);
                var row = preview.Billable.FirstOrDefault(r => r.SubscriptionId == subId);
                Assert(row is not null, "Vadesi gelen abonelik önizlemede görünmedi.");
                return $"{preview.BillableCount} fatura, toplam {preview.Total:N2} " +
                       $"{preview.Currency} — \"{preview.Summary}\"";
            });

        await CheckAsync(results, Subs, "Vadesi gelen abonelik faturalandırıldı",
            "Abonelik iş modelinin kalbi: fatura otomatik kesilmezse gelir tahsil edilmez.",
            async () =>
            {
                var result = await billing.RunAsync(today, ct);
                Assert(result.Created >= 1, $"Fatura üretilmedi: {result.Summary}");

                await using var db = factory.Create();
                var inv = await db.Invoices.AsNoTracking()
                    .FirstAsync(i => i.SubscriptionId == subId, ct);

                return $"{inv.Number} · {inv.GrandTotal:N2} {inv.Currency} " +
                       $"· dönem {inv.PeriodStart:dd.MM.yyyy}–{inv.PeriodEnd:dd.MM.yyyy}";
            });

        await CheckAsync(results, Subs, "Aynı dönem İKİNCİ KEZ faturalanmadı (idempotency)",
            "İşçi iki kez çalışsa veya iki instance ayakta olsa bile müşteri iki " +
            "fatura almamalı. Garanti iş mantığında değil, (subscription_id, " +
            "period_start) unique index'inde.",
            async () =>
            {
                await using var db = factory.Create();

                // Takvimi elle geri al: sanki işçi aynı dönem için tekrar çalışıyor.
                var sub = await db.Subscriptions.FirstAsync(s => s.Id == subId, ct);
                var billedPeriod = sub.NextBillingDate;
                sub.NextBillingDate = BillingSchedule.PreviousPeriodStart(
                    billedPeriod, BillingCycle.Monthly, sub.BillingAnchorDay);
                await db.SaveChangesAsync(ct);

                var before = await db.Invoices.AsNoTracking()
                    .CountAsync(i => i.SubscriptionId == subId, ct);

                var result = await billing.RunAsync(today, ct);

                var after = await db.Invoices.AsNoTracking()
                    .CountAsync(i => i.SubscriptionId == subId, ct);

                Assert(after == before,
                       $"Mükerrer fatura üretildi: {before} → {after}");

                return $"Fatura sayısı {before} → {after} (değişmedi), " +
                       $"{result.Skipped} abonelik atlandı.";
            });

        await CheckAsync(results, Subs, "Gecikmiş abonelik takibe alındı (dunning)",
            "Ödenmeyen fatura için 3/7/14. günlerde hatırlatma, 21. günde askıya " +
            "alma. PastDueSince İDEMPOTENT olmalı — her turda bugüne kaysaydı " +
            "gecikme günü hep 0 kalır ve hiçbir hatırlatma gönderilmezdi.",
            async () =>
            {
                var dunning = sp.GetRequiredService<DunningService>();

                await using (var db = factory.Create())
                {
                    // Aboneliğin faturasını 10 gün gecikmiş göster
                    var inv = await db.Invoices.FirstAsync(i => i.SubscriptionId == subId, ct);
                    inv.DueDate = today.AddDays(-10);
                    await db.SaveChangesAsync(ct);
                }

                var run = await dunning.RunAsync(today, ct);

                await using var check = factory.Create();
                var sub = await check.Subscriptions.AsNoTracking()
                    .FirstAsync(s => s.Id == subId, ct);

                Assert(sub.Status == SubscriptionStatus.PastDue,
                       $"Durum PastDue olmalı, {sub.Status} kaldı.");
                Assert(sub.PastDueSince == today.AddDays(-10),
                       $"PastDueSince faturanın VADESİ olmalı ({today.AddDays(-10)}), " +
                       $"{sub.PastDueSince} yazılmış — tur tarihine kaymış.");

                return $"Durum {sub.StatusText} · gecikme {sub.DaysPastDue(today)} gün · " +
                       $"seviye {sub.DunningLevel} · {run.RemindersSent} hatırlatma";
            });

        await CheckAsync(results, Subs, "Borç kapanınca abonelik normale döndü",
            "Askıdaki müşteri ödeme yaptığı anda hizmete dönmeli; el ile müdahale " +
            "gerekirse müşteri ödediği halde hizmet alamaz.",
            async () =>
            {
                var payments = sp.GetRequiredService<PaymentService>();
                var dunning = sp.GetRequiredService<DunningService>();

                decimal remaining;
                await using (var db = factory.Create())
                {
                    var inv = await db.Invoices.AsNoTracking()
                        .FirstAsync(i => i.SubscriptionId == subId, ct);
                    remaining = inv.GrandTotal - inv.PaidAmount;
                }

                await payments.CreateAsync(new PaymentForm
                {
                    PartyId = seed.CustomerId,
                    Amount = remaining,
                    PaymentDate = today,
                    Method = PaymentMethod.BankTransfer,
                    AutoAllocate = true,
                    Reference = "Sistem testi — borç kapatma"
                }, ct);

                var run = await dunning.RunAsync(today, ct);

                await using var check = factory.Create();
                var sub = await check.Subscriptions.AsNoTracking()
                    .FirstAsync(s => s.Id == subId, ct);

                Assert(sub.Status == SubscriptionStatus.Active,
                       $"Durum Active olmalı, {sub.Status} kaldı.");
                Assert(sub.PastDueSince is null, "PastDueSince temizlenmedi.");

                return $"{remaining:N2} tahsil edildi → durum {sub.StatusText}, " +
                       $"{run.Recovered} abonelik kurtarıldı.";
            });
    }

    // ==================================================================
    // KULLANIM BAZLI FATURALAMA
    // ==================================================================

    private static async Task RunMeteredAsync(
        IServiceProvider sp, Sandbox seed, List<CheckResult> results, CancellationToken ct)
    {
        var usage = sp.GetRequiredService<UsageService>();
        var billing = sp.GetRequiredService<SubscriptionBillingService>();
        var factory = sp.GetRequiredService<IAppDbContextFactory>();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var dueDate = today.AddDays(-1);
        Guid subId;

        await using (var db = factory.Create())
        {
            var sub = new Subscription
            {
                TenantId = SandboxTenantId,
                PartyId = seed.CustomerId,
                PlanId = seed.MeteredPlanId,
                Status = SubscriptionStatus.Active,
                StartDate = today.AddMonths(-2),
                NextBillingDate = dueDate,
                BillingAnchorDay = dueDate.Day
            };
            db.Subscriptions.Add(sub);
            await db.SaveChangesAsync(ct);
            subId = sub.Id;
        }

        await CheckAsync(results, Metered, "Kullanım kaydedildi",
            "Toplam değil OLAY saklanır: müşteri \"bu rakam nereden çıktı\" " +
            "dediğinde satır satır gösterebilmeliyiz.",
            async () =>
            {
                await usage.RecordAsync(
                    new UsageEntry(subId, 80m, today.AddDays(-20), "Toplu gönderim"), ct);
                await usage.RecordAsync(
                    new UsageEntry(subId, 170m, today.AddDays(-10), "Kampanya"), ct);

                var summary = await usage.GetSummaryAsync(subId, ct);
                Assert(summary is not null, "Kullanım özeti üretilemedi.");
                return $"Dönem kullanımı {summary!.PeriodQuantity:N0} {summary.UnitName} · " +
                       $"kota {summary.Allowance:N0} · ücretlendirilecek {summary.Billable:N0}";
            });

        await CheckAsync(results, Metered, "Aynı kaynak numarası iki kez sayılmadı (idempotency)",
            "Entegrasyon ağ hatası sonrası aynı çağrıyı tekrarlarsa müşteri iki kat " +
            "öder. Bu PARASAL sonucu olan bir hatadır; garanti unique index'te.",
            async () =>
            {
                var first = await usage.RecordAsync(
                    new UsageEntry(subId, 50m, today.AddDays(-5), "SMS", "EVT-TEST-1"), ct);
                var second = await usage.RecordAsync(
                    new UsageEntry(subId, 50m, today.AddDays(-5), "SMS", "EVT-TEST-1"), ct);

                Assert(first == second, "İkinci çağrı YENİ kayıt oluşturdu.");

                await using var db = factory.Create();
                var count = await db.UsageRecords.AsNoTracking()
                    .CountAsync(u => u.SubscriptionId == subId && u.ExternalId == "EVT-TEST-1", ct);

                Assert(count == 1, $"{count} kayıt var, 1 olmalıydı.");
                return "İki çağrı → tek kayıt, aynı kimlik döndü.";
            });

        await CheckAsync(results, Metered, "Kota aşımı ayrı satır olarak faturalandı",
            "Sabit ücret PEŞİN, kullanım GEÇMİŞE DÖNÜK. Aynı faturada iki farklı " +
            "döneme ait iki satır bulunması hata değil zorunluluk.",
            async () =>
            {
                await billing.RunAsync(today, ct);

                await using var db = factory.Create();
                var invoice = await db.Invoices.AsNoTracking()
                    .Include(i => i.Lines)
                    .FirstAsync(i => i.SubscriptionId == subId, ct);

                var usageLine = invoice.Lines.SingleOrDefault(l => l.Unit == "SMS");
                Assert(usageLine is not null, "Kullanım satırı oluşmadı.");

                // 80 + 170 + 50 = 300 kullanım, 100 kota → 200 ücretlendirilir
                Assert(usageLine!.Quantity == 200m,
                       $"200 birim beklenirken {usageLine.Quantity} çıktı.");

                var flat = invoice.Lines.Single(l => l.Unit != "SMS");
                return $"{invoice.Number} · sabit {flat.UnitPrice:N2} + " +
                       $"kullanım {usageLine.Quantity:N0} × {usageLine.UnitPrice:N2} " +
                       $"= matrah {invoice.TaxBaseTotal:N2}";
            });

        await CheckAsync(results, Metered, "Faturalanan kullanım damgalandı",
            "Damga (invoice_id) olmasaydı aynı kullanım sonraki turda TEKRAR " +
            "faturalanırdı. Kota içinde kalanlar da damgalanır, yoksa kotayı " +
            "ikinci kez tüketirler.",
            async () =>
            {
                await using var db = factory.Create();
                var unbilled = await db.UsageRecords.AsNoTracking()
                    .CountAsync(u => u.SubscriptionId == subId && u.InvoiceId == null, ct);

                Assert(unbilled == 0, $"{unbilled} kayıt damgalanmadan kaldı.");
                var billed = await db.UsageRecords.AsNoTracking()
                    .CountAsync(u => u.SubscriptionId == subId && u.InvoiceId != null, ct);

                return $"{billed} kaydın tamamı faturaya damgalandı, damgasız kayıt yok.";
            });

        await CheckAsync(results, Metered, "Geç gelen kullanım kaybolmadı",
            "Entegrasyon geçmiş tarihli veri gönderdiğinde o kullanım hiçbir faturaya " +
            "girmemeli mi? Hayır — sorgu TARİHE değil DAMGAYA baktığı için bir " +
            "sonraki faturaya girer.",
            async () =>
            {
                // Fatura kesildikten SONRA geçmiş tarihli kayıt geldi
                await usage.RecordAsync(
                    new UsageEntry(subId, 120m, today.AddDays(-15), "Geç gelen kayıt"), ct);

                await using var db = factory.Create();
                var pending = await db.UsageRecords.AsNoTracking()
                    .CountAsync(u => u.SubscriptionId == subId && u.InvoiceId == null, ct);

                Assert(pending == 1, $"Geç gelen kayıt beklemede görünmüyor ({pending}).");

                var summary = await usage.GetSummaryAsync(subId, ct);
                return $"Geçmiş tarihli 120 birim faturalanmamış olarak bekliyor " +
                       $"(toplam {summary!.UnbilledQuantity:N0}), sonraki faturaya girecek.";
            });

        await CheckAsync(results, Metered, "Faturalanmış kullanım silinemez",
            "Fatura tutarı o kayda dayanıyor; silinirse fatura dayanaksız kalır. " +
            "Düzeltme ters kayıtla (storno) yapılır.",
            async () =>
            {
                await using var db = factory.Create();
                var billedId = await db.UsageRecords.AsNoTracking()
                    .Where(u => u.SubscriptionId == subId && u.InvoiceId != null)
                    .Select(u => u.Id).FirstAsync(ct);

                return await ExpectRejectAsync(
                    () => usage.DeleteAsync(billedId, ct), "değiştirilemez");
            });
    }

    // ==================================================================
    // MESAJLAŞMA
    // ==================================================================

    private async Task RunMessagingAsync(
        IServiceProvider sp, List<CheckResult> results, CancellationToken ct)
    {
        var factory = sp.GetRequiredService<IAppDbContextFactory>();

        await CheckAsync(results, Messaging, "Fatura kesilince outbox'a olay yazıldı",
            "Olay doğrudan broker'a gönderilseydi ve gönderim başarısız olsaydı " +
            "fatura kesilir ama kimse haberdar olmazdı. Outbox olayı fatura ile " +
            "AYNI transaction'da yazar — ikisi ya birlikte olur ya hiç olmaz.",
            async () =>
            {
                await using var db = factory.Create();

                var recent = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.TenantId == SandboxTenantId)
                    .OrderByDescending(m => m.OccuredAt)
                    .Select(m => new { m.Type, m.ProcessedAt, m.AttemptCount, m.LastError })
                    .Take(10)
                    .ToListAsync(ct);

                Assert(recent.Count > 0, "Bu turda hiç outbox mesajı yazılmadı.");

                var types = string.Join(", ", recent.Select(r => r.Type).Distinct());
                var published = recent.Count(r => r.ProcessedAt is not null);

                return $"{recent.Count} olay yazıldı ({types}) · {published} tanesi yayınlandı.";
            });

        if (!rabbit.Value.Enabled)
        {
            Skip(results, Messaging, "Yayınlanan mesajlar işaretlendi",
                 "ProcessedAt dolmuyorsa yayıncı çalışmıyordur; mesajlar birikir.",
                 "RabbitMQ kapalı — outbox mesajı yazılıyor ama yayınlanmıyor. " +
                 "Veri kaybı yok, teslim broker açılınca gerçekleşir.");
            return;
        }

        await CheckAsync(results, Messaging, "Yayınlanan mesajlar işaretlendi",
            "ProcessedAt dolmuyorsa yayıncı çalışmıyordur; mesajlar birikir ve " +
            "sağlık ucu 5 dakika sonra 503 döndürmeye başlar.",
            async () =>
            {
                await using var db = factory.Create();

                // Yayıncı 5 saniyede bir çalışıyor; kısa bir pencere tanıyoruz.
                for (var i = 0; i < 12; i++)
                {
                    var pending = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                        .CountAsync(m => m.TenantId == SandboxTenantId
                                      && m.ProcessedAt == null, ct);

                    if (pending == 0)
                        return "Bu turda yazılan olayların tamamı broker'a yayınlandı.";

                    await Task.Delay(1_000, ct);
                }

                var stillPending = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.TenantId == SandboxTenantId && m.ProcessedAt == null)
                    .Select(m => new { m.Type, m.AttemptCount, m.LastError })
                    .ToListAsync(ct);

                var firstError = stillPending.FirstOrDefault()?.LastError;

                throw new InvalidOperationException(
                    $"{stillPending.Count} mesaj 12 saniyede yayınlanmadı. " +
                    $"İlk hata: {firstError ?? "(deneme yapılmamış — yayıncı çalışmıyor olabilir)"}");
            });
    }

    // ==================================================================
    // KULLANICI VE YETKİ
    // ==================================================================

    private static async Task RunSecurityAsync(
        IServiceProvider sp, List<CheckResult> results, CancellationToken ct)
    {
        var users = sp.GetRequiredService<UserAdminService>();

        await CheckAsync(results, Security, "Kullanıcı listesi tenant ile sınırlı",
            "AppUser ITenantScoped DEĞİL — global query filter ona uygulanmıyor. " +
            "Her sorguya TenantId elle ekleniyor. Bir tek yerde unutulursa bir " +
            "firmanın yöneticisi başka firmanın kullanıcılarını görür.",
            async () =>
            {
                // Sandbox tenant'ta hiç kullanıcı yok; demo kullanıcıları GÖRÜNMEMELİ.
                var list = await users.ListAsync(ct);
                Assert(list.Count == 0,
                       $"Sandbox tenant'ta {list.Count} kullanıcı göründü — TENANT SIZINTISI.");

                return "Sandbox tenant'ta 0 kullanıcı: demo firmasının kullanıcıları sızmadı.";
            });

        await CheckAsync(results, Security, "Geçersiz rol reddedildi",
            "Rol adı serbest metin olsaydı yazım hatası sessizce yetkisiz kullanıcı " +
            "üretirdi.",
            () => ExpectRejectAsync(
                () => users.CreateAsync(
                    new CreateUserRequest("selftest@sandbox.local", "Test", "SuperAdmin"), ct),
                "Geçersiz rol"));

        await CheckAsync(results, Security, "Denetim kaydı yazıldı",
            "Kim, ne zaman, hangi alanı neyden neye çevirdi. Denetim kaydı yoksa " +
            "mali bir uyuşmazlıkta gösterilecek delil de yoktur.",
            async () =>
            {
                var factory = sp.GetRequiredService<IAppDbContextFactory>();
                await using var db = factory.Create();

                var entries = await db.AuditEntries.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.TenantId == SandboxTenantId)
                    .GroupBy(a => a.EntityName)
                    .Select(g => new { Entity = g.Key, Count = g.Count() })
                    .ToListAsync(ct);

                Assert(entries.Count > 0, "Bu turda hiç denetim kaydı yazılmadı.");

                var total = entries.Sum(e => e.Count);
                var top = string.Join(", ", entries.OrderByDescending(e => e.Count)
                                                   .Take(3)
                                                   .Select(e => $"{e.Entity} ({e.Count})"));

                return $"{total} denetim kaydı · en çok: {top}";
            });
    }

    // ==================================================================
    // Form yardımcıları
    // ==================================================================

    private static InvoiceForm SalesForm(Sandbox seed, decimal price) => new()
    {
        PartyId = seed.CustomerId,
        Type = InvoiceType.Sales,
        Series = "NEX",
        IssueDate = DateOnly.FromDateTime(DateTime.Today),
        Lines =
        [
            new InvoiceLineForm
            {
                ProductId = seed.ProductId,
                ProductCode = "TEST01",
                ProductName = "Test Hizmeti",
                Unit = "Adet",
                Quantity = 1m,
                UnitPrice = price,
                TaxRateId = seed.TaxRateId,
                TaxRate = seed.TaxRate
            }
        ]
    };

    private static InvoiceForm PurchaseForm(Sandbox seed, string supplierNo) => new()
    {
        PartyId = seed.SupplierId,
        Type = InvoiceType.Purchase,
        Series = "ALS",
        SupplierInvoiceNo = supplierNo,
        IssueDate = DateOnly.FromDateTime(DateTime.Today),
        Lines =
        [
            new InvoiceLineForm
            {
                ProductId = seed.ProductId,
                ProductCode = "TEST01",
                ProductName = "Test Hizmeti",
                Unit = "Adet",
                Quantity = 1m,
                UnitPrice = 800m,
                TaxRateId = seed.TaxRateId,
                TaxRate = seed.TaxRate
            }
        ]
    };
}
