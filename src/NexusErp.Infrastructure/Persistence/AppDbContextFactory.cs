using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusErp.Application.Abstractions;

namespace NexusErp.Infrastructure.Persistence;

/// <summary>
/// SADECE tasarım zamanı (dotnet ef ...) için. AppDbContext'in 3 parametresi olduğu için
/// bu factory olmadan "Unable to create a DbContext" hatası alırsın.
/// Uygulama çalışırken devreye girmez.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=nexuserp;Username=nexus;Password=nexus_dev_2026")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new DesignTimeTenant(), new DesignTimeUser());
    }

    private sealed class DesignTimeTenant : ITenantContext
    {
        public Guid TenantId => Guid.Empty;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class DesignTimeUser : ICurrentUser
    {
        public string UserName => "migration";
    }
}
