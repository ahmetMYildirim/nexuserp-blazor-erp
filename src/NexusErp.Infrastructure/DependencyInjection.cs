using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusErp.Application.Abstractions;
using Microsoft.AspNetCore.Identity;
using NexusErp.Infrastructure.Documents;
using NexusErp.Infrastructure.EInvoice;
using NexusErp.Infrastructure.Identity;
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

        // Tenant/kullanıcı tutucuları. Değeri barındıran uygulama yazar:
        // Blazor → AuthStateInitializer, API → AddApiIdentityContext() (aşağıda).
        services.AddHttpContextAccessor();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<MutableCurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<MutableCurrentUser>());

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

        services.AddIdentity<AppUser, IdentityRole<Guid>>(opt =>
                {
                    opt.Password.RequiredLength = 8;
                    opt.Password.RequireNonAlphanumeric = true;
                    opt.User.RequireUniqueEmail = true;
                    opt.SignIn.RequireConfirmedAccount = false;
                    opt.Lockout.MaxFailedAccessAttempts = 5;
                    opt.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                })
                .AddEntityFrameworkStores<AppDbContext>()
                .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
                .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(opt =>
        {
            opt.LoginPath = "/giris";
            opt.LogoutPath = "/cikis";
            opt.AccessDeniedPath = "/yetkisiz";
            opt.ExpireTimeSpan = TimeSpan.FromHours(8);
            opt.SlidingExpiration = true;
            opt.Cookie.Name = "NexusErp.Auth";
        });
        services.AddScoped<IInvoicePdfGenerator, InvoicePdfGenerator>();
        services.AddSingleton<IExcelExporter, ExcelExporter>();
        services.AddScoped<IUblInvoiceBuilder, UblInvoiceBuilder>();
        services.AddSingleton<IEInvoiceGateway, MockEInvoiceGateway>();

        return services;
    }

    /// <summary>
    /// REST API için: tenant ve kullanıcı HttpContext'teki JWT claim'lerinden okunur.
    /// Blazor bunu ÇAĞIRMAZ — orada devre bazlı AuthStateInitializer kullanılıyor.
    /// </summary>
    public static IServiceCollection AddApiIdentityContext(this IServiceCollection services)
    {
        services.AddScoped<ITenantContext, ClaimsTenantContext>();
        services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        return services;
    }
}
