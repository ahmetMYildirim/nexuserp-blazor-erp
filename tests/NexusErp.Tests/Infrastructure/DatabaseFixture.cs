using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Infrastructure.Persistence;
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

        // Şemayı bir kez kur; testler farklı tenant'larla aynı şemayı paylaşır
        await using var db = CreateContext(Guid.CreateVersion7());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public AppDbContext CreateContext(Guid tenantId)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention();

        if (Environment.GetEnvironmentVariable("NEXUS_SQL_LOG") is { Length: > 0 } path)
            builder.LogTo(line => File.AppendAllText(path, line + Environment.NewLine),
                          Microsoft.Extensions.Logging.LogLevel.Information)
                   .EnableSensitiveDataLogging();

        var options = builder.Options;

        return new AppDbContext(options, new FakeTenant(tenantId), new FakeUser());
    }

    /// <summary>InvoiceNumberGenerator gibi ITenantContext isteyen servisler için.</summary>
    public ITenantContext CreateTenantContext(Guid tenantId) => new FakeTenant(tenantId);

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
