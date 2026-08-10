using Microsoft.Extensions.DependencyInjection;
using NexusErp.Application.Parties;
using NexusErp.Application.Products;

namespace NexusErp.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Application servisleri. MediatR yok (v13+ ticari lisans) — düz servis sınıfları:
    /// stack trace okunabilir kalıyor, "handler nerede?" problemi olmuyor (ADR-002).
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<PartyService>();
        services.AddScoped<ProductService>();

        return services;
    }
}
