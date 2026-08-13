using Microsoft.Extensions.DependencyInjection;
using NexusErp.Application.Auditing;
using NexusErp.Application.Dashboard;
using NexusErp.Application.Messaging;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Payments;
using NexusErp.Application.Parties;
using NexusErp.Application.Products;
using NexusErp.Application.Subscriptions;
using NexusErp.Application.TaxRates;

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
        services.AddScoped<InvoiceService>();
        services.AddScoped<TaxRateService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<SubscriptionBillingService>();
        services.AddScoped<DunningService>();
        services.AddScoped<UsageService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PartyBalanceService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<AuditService>();
        services.AddScoped<OutboxHealthService>();
        services.AddScoped<DemoDataSeeder>();

        // .NET 8 zaman soyutlaması. Testte FakeTimeProvider ile zamanı ileri sarabiliyoruz —
        // Bölüm 09'da abonelik döngüsünü test etmenin başka yolu yok.
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
