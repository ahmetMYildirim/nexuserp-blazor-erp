using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Subscriptions;
using NexusErp.Infrastructure.Persistence;

namespace NexusErp.Infrastructure.BackgroundJobs;

/// <summary>
/// Ödenmeyen abonelik faturalarını günde bir tarar.
///
/// SubscriptionBillingWorker ile aynı iskelet: her tenant için ayrı scope,
/// tenant ayarlanmadan çalışırsa yalnızca varsayılan tenant taranır.
/// </summary>
public sealed class DunningWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<DunningWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Dunning servisi başlatıldı.");

        // Açılışta hemen bir tur — demo için pratik
        await RunSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunSafelyAsync(stoppingToken);
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var tenantIds = await GetActiveTenantIdsAsync(scope.ServiceProvider, ct);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().Date);

            foreach (var tenantId in tenantIds)
            {
                await using var tenantScope = scopeFactory.CreateAsyncScope();
                tenantScope.ServiceProvider.GetRequiredService<ITenantContext>()
                                           .SetTenant(tenantId);

                var dunning = tenantScope.ServiceProvider.GetRequiredService<DunningService>();
                await dunning.RunAsync(today, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // uygulama kapanıyor — normal
        }
        catch (Exception ex)
        {
            // ⚠️ Bu catch olmazsa servis sessizce ölür ve takip bir daha çalışmaz.
            logger.LogError(ex, "Dunning turunda beklenmeyen hata.");
        }
    }

    private static async Task<List<Guid>> GetActiveTenantIdsAsync(
        IServiceProvider provider, CancellationToken ct)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        return await db.Tenants.IgnoreQueryFilters()
                       .Where(t => t.IsActive && !t.IsDeleted)
                       .Select(t => t.Id)
                       .ToListAsync(ct);
    }
}
