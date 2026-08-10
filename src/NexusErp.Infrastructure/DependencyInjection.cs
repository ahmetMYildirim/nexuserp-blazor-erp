using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusErp.Application.Abstractions;
using NexusErp.Infrastructure.Documents;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Infrastructure.Tenancy;

namespace NexusErp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TenantOptions>(config.GetSection(TenantOptions.SectionName));

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentUser, StubCurrentUser>();   // Bölüm 12'de gerçeğiyle değişecek

        services.AddDbContext<AppDbContext>(opt =>
        {
            opt.UseNpgsql(config.GetConnectionString("Postgres"), npg =>
                {
                    npg.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                    npg.EnableRetryOnFailure(3);   // container yeniden başlarken uygulama çökmesin
                })
               // Party.TaxNumber → parties.tax_number
               // Bu olmadan PostgreSQL'de her sorguda çift tırnak kullanmak zorunda kalırsın
               .UseSnakeCaseNamingConvention();
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IInvoiceNumberGenerator, InvoiceNumberGenerator>();
        services.AddScoped<IInvoicePdfGenerator, InvoicePdfGenerator>();
        services.AddSingleton<IExcelExporter, ExcelExporter>();

        return services;
    }
}
