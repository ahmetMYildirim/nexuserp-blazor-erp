using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusErp.Application.Events;
using NexusErp.Domain.Entities;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NexusErp.Infrastructure.BackgroundJobs;

/// <summary>
/// InvoiceIssued olayını dinler. Şimdilik yalnızca logluyor — asıl amacı zincirin
/// uçtan uca çalıştığını göstermek. Gerçek iş (e-posta, entegratöre gönderim,
/// muhasebe fişi) buraya ya da ayrı tüketicilere eklenir.
/// </summary>
public sealed class InvoiceIssuedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<InvoiceIssuedConsumer> logger) : BackgroundService
{
    private const string QueueName = "nexuserp.invoice-issued";
    private const string ConsumerName = "invoice-issued";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("RabbitMQ kapalı — InvoiceIssued tüketicisi başlatılmadı.");
            return;
        }

        try
        {
            await ConsumeAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // uygulama kapanıyor — normal
        }
        catch (Exception ex)
        {
            // Broker kapalıysa uygulama yine de ayakta kalmalı.
            logger.LogError(ex, "InvoiceIssued tüketicisi başlatılamadı.");
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Value.Uri),
            ClientProvidedName = "nexuserp-consumer",
            AutomaticRecoveryEnabled = true
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        // ⚠️ Ana exchange'i tüketici de tanımlamalı. Yayıncı tembel çalışıyor
        // (ilk mesajda bağlanıyor); tüketici uygulama açılışında ayağa kalktığı
        // için exchange henüz yoksa QueueBind "NOT_FOUND - no exchange" der ve
        // tüketici hiç başlamaz. ExchangeDeclare idempotenttir: aynı parametrelerle
        // ikinci çağrı hiçbir şey yapmaz.
        await channel.ExchangeDeclareAsync(options.Value.Exchange,
            ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        // Ölü mektup kuyruğu: 5 kez işlenemeyen mesaj burada birikir, kaybolmaz.
        await channel.ExchangeDeclareAsync(options.Value.DeadLetterExchange,
            ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{QueueName}.dlq",
            durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

        await channel.QueueBindAsync($"{QueueName}.dlq", options.Value.DeadLetterExchange,
            routingKey: "", cancellationToken: ct);

        var args = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = options.Value.DeadLetterExchange
        };

        await channel.QueueDeclareAsync(QueueName,
            durable: true, exclusive: false, autoDelete: false,
            arguments: args, cancellationToken: ct);

        await channel.QueueBindAsync(QueueName, options.Value.Exchange,
            routingKey: $"nexuserp.{nameof(InvoiceIssued)}", cancellationToken: ct);

        // ⚠️ Prefetch. Bu olmadan RabbitMQ tüm kuyruğu tek tüketiciye fırlatır;
        // 10.000 mesaj varsa hepsi belleğe gelir ve ikinci instance boş oturur.
        await channel.BasicQosAsync(0, prefetchCount: 10, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            string? messageId = ea.BasicProperties.MessageId;
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.Span);
                var evt = JsonSerializer.Deserialize<InvoiceIssued>(json)
                          ?? throw new InvalidOperationException("Boş gövde.");

                // ⚠️ IDEMPOTENCY. Outbox "en az bir kez" teslim eder: işçi mesajı
                // yayınlayıp ProcessedAt'i yazmadan çökerse aynı mesaj tekrar gelir.
                // Yan etkili işi (e-posta, muhasebe fişi) İKİ KEZ yapmamak için
                // önce deftere yazmayı deniyoruz; unique index ihlali gelirse mesaj
                // zaten işlenmiştir ve atlanır.
                if (!await TryMarkProcessedAsync(ea, ct))
                {
                    logger.LogDebug("Mesaj zaten işlenmiş, atlandı: {MessageId}", messageId);
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false,
                                                cancellationToken: ct);
                    return;
                }

                logger.LogInformation(
                    "Fatura kesildi olayı alındı: {Number} — {Total} {Currency} ({MessageId})",
                    evt.Number, evt.GrandTotal, evt.Currency, messageId);

                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mesaj işlenemedi: {MessageId}", messageId);

                // ⚠️ requeue: false. true verirsen bozuk mesaj sonsuz döngüye girer
                // (al → patla → kuyruğa dön → al...) ve CPU'yu yer. DLQ'ya düşsün.
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false,
                                             requeue: false, cancellationToken: ct);
            }
        };

        await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer,
                                        cancellationToken: ct);

        logger.LogInformation("InvoiceIssued tüketicisi dinlemede: {Queue}", QueueName);

        // ⚠️ Burada beklemek ZORUNLU. Metot dönerse `await using` connection ve
        // channel dispose edilir, tüketici sessizce ölür ve kuyruk birikir.
        await Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>
    /// İdempotency defterine yazmayı dener. <c>false</c> dönerse mesaj daha önce
    /// işlenmiştir.
    ///
    /// ⚠️ Garanti "önce sorgula sonra yaz" kontrolünde değil, veri tabanındaki
    /// <c>(consumer_name, message_id)</c> unique index'inde. İki tüketici örneği
    /// aynı mesajı aynı anda alırsa sorgu ikisine de "yok" der; index yanılmaz.
    /// </summary>
    private async Task<bool> TryMarkProcessedAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        if (!Guid.TryParse(ea.BasicProperties.MessageId, out var messageId))
            return true;   // kimliği yoksa tekilleştiremeyiz, işle geç

        Guid.TryParse(TenantHeader(ea), out var tenantId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            ConsumerName = ConsumerName,
            MessageId = messageId,
            TenantId = tenantId,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return false;   // zaten işlenmiş — hata değil, idempotency çalışıyor
        }
    }

    private static string? TenantHeader(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties.Headers is null) return null;
        if (!ea.BasicProperties.Headers.TryGetValue("tenant-id", out var raw)) return null;

        // RabbitMQ header'ları byte[] olarak taşır
        return raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string s => s,
            _ => raw?.ToString()
        };
    }

    /// <summary>PostgreSQL 23505 = unique_violation.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name == "PostgresException"
           && ex.InnerException.GetType().GetProperty("SqlState")?
                 .GetValue(ex.InnerException) as string == "23505";

}
