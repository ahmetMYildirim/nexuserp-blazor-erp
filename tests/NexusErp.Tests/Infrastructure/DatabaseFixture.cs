using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Infrastructure.Persistence.Seed;
using Testcontainers.PostgreSql;

namespace NexusErp.Tests.Infrastructure;

/// <summary>
/// Gerçek PostgreSQL üzerinde entegrasyon testi. InMemory sağlayıcı kullanmıyoruz:
/// partial index, ILIKE, precision gibi davranışları taklit edemez — yani testler
/// geçer ama üretimde patlar.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        // Üretimle AYNI locale — yoksa Türkçe arama testi yanlış sonuç verir
        // (docker-compose.yml ile birebir aynı olmalı)
        .WithEnvironment("POSTGRES_INITDB_ARGS",
            "--locale-provider=icu --icu-locale=tr-TR --locale=C.UTF-8 --encoding=UTF8")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    // xUnit v2: IAsyncLifetime Task döner (v3'te ValueTask)
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Şemayı bir kez kur; testler farklı tenant'larla aynı şemayı paylaşır.
        // ⚠️ Provizyonsuz context: hesap planı tohumu tablolar HENÜZ YOKKEN
        // çalışamaz ("relation accounts does not exist").
        await using var db = NewContext(Guid.CreateVersion7());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public AppDbContext CreateContext(Guid tenantId) => NewContext(tenantId);

    private AppDbContext NewContext(Guid tenantId)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention();

        if (Environment.GetEnvironmentVariable("NEXUS_SQL_LOG") is { Length: > 0 } path)
            builder.LogTo(line => File.AppendAllText(path, line + Environment.NewLine),
                          Microsoft.Extensions.Logging.LogLevel.Information)
                   .EnableSensitiveDataLogging();

        return new AppDbContext(builder.Options, new FakeTenant(tenantId), new FakeUser());
    }

    /// <summary>
    /// Tenant'ın hesap planını kurar. Fatura kesen / tahsilat işleyen testler
    /// bunu ÇAĞIRMAK ZORUNDA: o akışlar artık otomatik muhasebe fişi üretiyor
    /// ve fiş, hesap planı yoksa üretilemiyor (bilerek öyle — sessizce fiş
    /// atlamak defterin eksik kalması demektir ve kimse fark etmez).
    ///
    /// ⚠️ CreateContext içinden OTOMATİK çağrılmıyor. Denenmişti: 92 hesaplık
    /// tohum her tenant'a 92 denetim kaydı ekliyor ve denetim testlerinin
    /// saydığı satırları bozuyor. Gizli global yan etki yerine ihtiyacı olan
    /// testin açıkça istemesi tercih edildi.
    /// </summary>
    public async Task SeedChartOfAccountsAsync(Guid tenantId)
    {
        await using var db = NewContext(tenantId);
        await ChartOfAccountsSeeder.EnsureAsync(db, tenantId);
    }

    /// <summary>Senkron kurulum metotları için.</summary>
    public void SeedChartOfAccounts(Guid tenantId)
        => SeedChartOfAccountsAsync(tenantId).GetAwaiter().GetResult();

    /// <summary>InvoiceNumberGenerator gibi ITenantContext isteyen servisler için.</summary>
    public ITenantContext CreateTenantContext(Guid tenantId) => new FakeTenant(tenantId);

    /// <summary>
    /// Fabrikaya geçmiş servisler için. Servis her çağrıda TAZE context açar —
    /// yazdığı veriyi doğrulamak istiyorsan testte ayrı bir CreateContext() aç.
    /// </summary>
    public IAppDbContextFactory CreateFactory(Guid tenantId) => new TestFactory(this, tenantId);

    private sealed class TestFactory(DatabaseFixture fixture, Guid tenantId) : IAppDbContextFactory
    {
        public IAppDbContext Create() => fixture.CreateContext(tenantId);
    }

    private sealed class FakeTenant(Guid id) : ITenantContext
    {
        public Guid TenantId { get; private set; } = id;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class FakeUser : ICurrentUser
    {
        public string UserName => "test";
    }
}

[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
