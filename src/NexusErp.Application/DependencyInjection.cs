using Microsoft.Extensions.DependencyInjection;
using NexusErp.Application.Parties;

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

        return services;
    }
}
