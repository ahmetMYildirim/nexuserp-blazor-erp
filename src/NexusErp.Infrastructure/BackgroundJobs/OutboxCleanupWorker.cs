using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusErp.Infrastructure.Persistence;

namespace NexusErp.Infrastructure.BackgroundJobs;

/// <summary>
/// İşlenmiş outbox satırlarını ve eski idempotency kayıtlarını siler.
///
/// Bu iş olmadan iki tablo sonsuza kadar büyür: her fatura bir outbox satırı,
/// her teslim bir idempotency satırı bırakır. Yıllar sonra tabloların boyutu
/// partial index'i bile yavaşlatır.
///
/// 30 gün, bir sorun olduğunda geriye dönüp bakmak için yeterli bir pencere.
/// </summary>
public sealed class OutboxCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<OutboxCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox temizlik işi başlatıldı (saklama: {Days} gün).",
                              Retention.TotalDays);

        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            await RunSafelyAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = clock.GetUtcNow() - Retention;

            // ⚠️ IgnoreQueryFilters: temizlik TÜM tenant'ları kapsamalı.
            // ExecuteDeleteAsync tek SQL DELETE üretir — milyonlarca satırı
            // belleğe çekmez ve SaveChanges'ten geçmediği için denetim kaydı da yazmaz.
            var outbox = await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            var processed = await db.ProcessedMessages
                .IgnoreQueryFilters()
                .Where(m => m.ProcessedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (outbox > 0 || processed > 0)
                logger.LogInformation(
                    "Temizlik: {Outbox} outbox, {Processed} idempotency kaydı silindi.",
                    outbox, processed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // uygulama kapanıyor — normal
        }
        catch (Exception ex)
        {
            // Catch olmazsa servis sessizce ölür ve temizlik bir daha çalışmaz.
            logger.LogError(ex, "Outbox temizliğinde beklenmeyen hata.");
        }
    }
}
