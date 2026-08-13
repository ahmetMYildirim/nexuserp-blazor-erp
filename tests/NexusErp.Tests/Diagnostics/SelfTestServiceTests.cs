using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusErp.Application;
using NexusErp.Application.Abstractions;
using NexusErp.Infrastructure.Diagnostics;
using NexusErp.Infrastructure.Identity;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Messaging;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Diagnostics;

/// <summary>
/// Sistem testi ekranının kendi testi.
///
/// ⚠️ "Testi test etmek" gereksiz gibi görünür ama değil: bu ekran demoda
/// "her şey çalışıyor" diye YEŞİL gösterecek. Kontrollerden biri sessizce
/// hiçbir şey doğrulamaz hale gelirse yeşil ekran YALAN söyler — sahte güven
/// hiç test olmamasından daha kötüdür.
///
/// Burada RabbitMQ kapalı çalıştırılıyor: broker'a bağlı iki kontrol ATLANDI
/// olarak işaretlenmeli, KALDI olarak değil.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SelfTestServiceTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private ServiceProvider _provider = default!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();

        services.AddDbContextFactory<AppDbContext>(o => o
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention(), ServiceLifetime.Scoped);

        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IAppDbContextFactory, AppDbContextFactoryAdapter>();

        services.AddScoped<ITenantContext, MutableTenant>();
        services.AddScoped<ICurrentUser, FixedUser>();
        services.AddScoped<IInvoiceNumberGenerator, InvoiceNumberGenerator>();

        services.AddIdentityCore<AppUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AppDbContext>()
                .AddDefaultTokenProviders();

        services.AddScoped<UserAdminService>();
        services.AddApplication();

        // ⚠️ Broker KAPALI: bağlı kontroller atlanmalı, kalmamalı.
        services.Configure<RabbitMqOptions>(o =>
        {
            o.Enabled = false;
            o.Uri = "amqp://guest:guest@localhost:5673";
            o.Exchange = "nexuserp.events";
        });

        // SMTP kontrolü gerçek bir sokete bağlanıyor; testte kapalı bir porta
        // yönlendirip kontrolün KALDI verdiğini de doğrulayabiliyoruz.
        services.Configure<SmtpOptions>(o =>
        {
            o.Host = "localhost";
            o.Port = 1025;
        });

        services.AddScoped<SelfTestService>();

        _provider = services.BuildServiceProvider();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private sealed class MutableTenant : ITenantContext
    {
        public Guid TenantId { get; private set; } = Guid.CreateVersion7();
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class FixedUser : ICurrentUser
    {
        public string UserName => "selftest";
    }

    private async Task<SelfTestRun> RunAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SelfTestService>().RunAsync();
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task Broker_disindaki_tum_kontroller_gecer()
    {
        var run = await RunAsync();

        var failed = run.Results.Where(r => r.Failed).ToList();

        // Hangi kontrolün neden kaldığını raporla — "1 test kaldı" demek yetmez.
        failed.ShouldBeEmpty(
            "KALAN KONTROLLER:\n" +
            string.Join("\n", failed.Select(f => $"  [{f.Category}] {f.Name}: {f.Detail}")));

        run.PassedCount.ShouldBeGreaterThan(20);
    }

    [Fact]
    public async Task Broker_kapaliyken_bagli_kontroller_atlanir_kalmaz()
    {
        var run = await RunAsync();

        // ⚠️ Kritik ayrım: yapılandırma tercihi HATA değildir. Broker kapalıyken
        // bu kontroller kırmızı gösterilseydi kullanıcı olmayan bir arıza arardı.
        var rabbit = run.Results.Single(r => r.Name == "RabbitMQ bağlantısı");
        rabbit.Outcome.ShouldBe(CheckOutcome.Skipped);

        var publish = run.Results.Single(r => r.Name == "Yayınlanan mesajlar işaretlendi");
        publish.Outcome.ShouldBe(CheckOutcome.Skipped);

        // Outbox'a YAZMA broker'dan bağımsız — o çalışmaya devam etmeli.
        run.Results.Single(r => r.Name.StartsWith("Fatura kesilince outbox"))
           .Outcome.ShouldBe(CheckOutcome.Passed);
    }

    [Fact]
    public async Task Her_kontrol_somut_cikti_uretir()
    {
        var run = await RunAsync();

        // ⚠️ "Başarılı" yazan bir kontrol hiçbir şey kanıtlamaz. Her satır
        // gerçek bir sayı/metin göstermeli ki okuyan kişi doğrulayabilsin.
        foreach (var result in run.Results)
        {
            result.Detail.ShouldNotBeNullOrWhiteSpace(
                $"{result.Name} kontrolü boş çıktı üretti.");
            result.Why.ShouldNotBeNullOrWhiteSpace(
                $"{result.Name} kontrolünün 'neden önemli' açıklaması yok.");
        }
    }

    [Fact]
    public async Task Tur_bitiminde_sandbox_verisi_silinir()
    {
        await RunAsync();

        await using var db = fixture.CreateContext(SelfTestService.SandboxTenantId);

        // ⚠️ Temizlik çalışmazsa her tur veri biriktirir ve ikinci tur
        // "zaten var" hatalarıyla dolar. Sandbox'ta TEK satır bile kalmamalı.
        (await db.Invoices.IgnoreQueryFilters()
            .CountAsync(i => i.TenantId == SelfTestService.SandboxTenantId)).ShouldBe(0);
        (await db.Parties.IgnoreQueryFilters()
            .CountAsync(p => p.TenantId == SelfTestService.SandboxTenantId)).ShouldBe(0);
        (await db.Subscriptions.IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == SelfTestService.SandboxTenantId)).ShouldBe(0);
        (await db.UsageRecords.IgnoreQueryFilters()
            .CountAsync(u => u.TenantId == SelfTestService.SandboxTenantId)).ShouldBe(0);
    }

    [Fact]
    public async Task Ust_uste_iki_tur_ayni_sonucu_verir()
    {
        var first = await RunAsync();
        var second = await RunAsync();

        // ⚠️ İkinci tur ilkinden kalan veri yüzünden patlarsa demo sırasında
        // "bir kez çalışıyor, ikincisinde bozuluyor" durumuna düşeriz.
        second.FailedCount.ShouldBe(first.FailedCount);
        second.PassedCount.ShouldBe(first.PassedCount);
    }
}
