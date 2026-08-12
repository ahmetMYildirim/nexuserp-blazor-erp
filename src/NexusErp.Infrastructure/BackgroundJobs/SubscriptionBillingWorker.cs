using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Subscriptions;
using NexusErp.Infrastructure.Persistence;
namespace NexusErp.Infrastructure.BackgroundJobs;

public sealed class SubscriptionBillingWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<SubscriptionBillingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Abonelik faturalandırma servisi başlatıldı.");
        await RunSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunSafelyAsync(stoppingToken);
    }
    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            // BackgroundService SINGLETON, DbContext SCOPED.
            await using var scope = scopeFactory.CreateAsyncScope();

            var tenantIds = await GetActiveTenantIdsAsync(scope.ServiceProvider, ct);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().Date);
            foreach (var tenantId in tenantIds)
            {
                await using var tenantScope = scopeFactory.CreateAsyncScope();
                tenantScope.ServiceProvider.GetRequiredService<ITenantContext>()
                                           .SetTenant(tenantId);
                var billing = tenantScope.ServiceProvider
                                         .GetRequiredService<SubscriptionBillingService>();
                await billing.RunAsync(today, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Abonelik faturalandırma turunda beklenmeyen hata.");
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
