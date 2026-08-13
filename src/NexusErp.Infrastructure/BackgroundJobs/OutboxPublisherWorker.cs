using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusErp.Application.Abstractions;
using NexusErp.Infrastructure.Persistence;

namespace NexusErp.Infrastructure.BackgroundJobs;

/// <summary>
/// Outbox tablosunu tarayıp bekleyen mesajları RabbitMQ'ya basar.
/// SubscriptionBillingWorker ile aynı iskelet; iki ek kuralı var (aşağıda).
/// </summary>
public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    TimeProvider clock,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private const int BatchSize = 20;
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox yayıncısı başlatıldı.");

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

            // ⚠️ KURAL 0 — EXECUTION STRATEGY.
            // AddDbContextFactory'de EnableRetryOnFailure(3) açık. Yeniden deneme
            // stratejisi varken BeginTransactionAsync doğrudan çağrılamaz:
            // "The configured execution strategy 'NpgsqlRetryingExecutionStrategy'
            //  does not support user-initiated transactions."
            // Tüm birim stratejinin içine sarılmalı ki geçici bir bağlantı hatasında
            // transaction'ın TAMAMI yeniden denensin.
            var strategy = db.Database.CreateExecutionStrategy();
            var (sent, failed) = await strategy.ExecuteAsync(() => PublishBatchAsync(db, ct));

            if (sent > 0 || failed > 0)
                logger.LogInformation("Outbox turu: {Sent} gönderildi, {Failed} hata.", sent, failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // uygulama kapanıyor — normal
        }
        catch (Exception ex)
        {
            // ⚠️ Bu catch OLMAZSA servis SESSİZCE ölür ve bir daha çalışmaz.
            logger.LogError(ex, "Outbox turunda beklenmeyen hata.");
        }
    }

    private async Task<(int Sent, int Failed)> PublishBatchAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            // ⚠️ KURAL 1 — AÇIK TRANSACTION ŞART.
            // FOR UPDATE SKIP LOCKED satır kilidi alır ama kilit TRANSACTION bitene
            // kadar yaşar. Transaction açmazsan EF her komutu kendi otomatik
            // transaction'ında çalıştırır: SELECT biter bitmez kilit bırakılır ve
            // ikinci bir instance aynı satırları okur → AYNI MESAJ İKİ KEZ yayınlanır.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // ⚠️ KURAL 2 — IgnoreQueryFilters() ŞART, iki ayrı sebeple:
            //   1) OutBoxMessage : ITenantScoped → global query filter var. EF filtreyi
            //      uygulamak için FromSql'i alt sorguya sarmak ister ama "FOR UPDATE"
            //      içeren SQL composable DEĞİLDİR:
            //      "'FromSql' was called with non-composable SQL".
            //   2) İşçi TÜM tenant'ların mesajlarını basmalı. Filtre açık kalsaydı
            //      yalnızca varsayılan tenant'ın mesajları gönderilirdi.
            var messages = await db.OutboxMessages
                .FromSql($"""
                    SELECT * FROM outbox_messages
                    WHERE processed_at IS NULL
                      AND attempt_count < {MaxAttempts}
                    ORDER BY occured_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .IgnoreQueryFilters()
                .ToListAsync(ct);

            if (messages.Count == 0)
            {
                await tx.CommitAsync(ct);
                return (0, 0);
            }

            var sent = 0;
            var failed = 0;

            foreach (var msg in messages)
            {
                try
                {
                    await publisher.PublishAsync(msg.Type, msg.Payload, msg.Id, msg.TenantId, ct);
                    msg.ProcessedAt = clock.GetUtcNow();
                    sent++;
                }
                catch (Exception ex)
                {
                    // Bir mesajın hatası diğerlerini ENGELLEMEZ.
                    msg.AttemptCount++;
                    msg.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    failed++;

                    logger.LogWarning(ex, "Outbox mesajı {Id} yayınlanamadı ({Attempt}/{Max}).",
                                      msg.Id, msg.AttemptCount, MaxAttempts);
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return (sent, failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (0, 0);
        }
    }
}
