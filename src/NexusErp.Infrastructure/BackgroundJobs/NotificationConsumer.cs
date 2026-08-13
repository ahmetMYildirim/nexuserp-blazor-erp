using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Events;
using NexusErp.Domain.Entities;
using NexusErp.Infrastructure.Messaging;
using NexusErp.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NexusErp.Infrastructure.BackgroundJobs;

/// <summary>
/// Bildirim tüketicisi — TÜM olayları dinler ve ilgilendiklerine e-posta gönderir.
///
/// Routing key <c>nexuserp.*</c>: topic exchange seçmemizin karşılığı burada.
/// Yeni bir olay eklendiğinde bu tüketiciye dokunmak gerekmez, otomatik yakalar;
/// yalnızca <see cref="Compose"/> içine bir dal eklenir.
///
/// ⚠️ Outbox zincirinin YAN ETKİSİ olan ilk tüketicisi. Aynı mesaj iki kez
/// gelirse müşteri iki e-posta alır — bu yüzden idempotency defteri şart.
/// </summary>
public sealed class NotificationConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<NotificationConsumer> logger) : BackgroundService
{
    private const string QueueName = "nexuserp.notifications";
    private const string ConsumerName = "notifications";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("RabbitMQ kapalı — bildirim tüketicisi başlatılmadı.");
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
            logger.LogError(ex, "Bildirim tüketicisi başlatılamadı.");
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Value.Uri),
            ClientProvidedName = "nexuserp-notifications",
            AutomaticRecoveryEnabled = true
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(options.Value.Exchange,
            ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.ExchangeDeclareAsync(options.Value.DeadLetterExchange,
            ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{QueueName}.dlq",
            durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync($"{QueueName}.dlq", options.Value.DeadLetterExchange,
            routingKey: "", cancellationToken: ct);

        await channel.QueueDeclareAsync(QueueName,
            durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = options.Value.DeadLetterExchange
            },
            cancellationToken: ct);

        // ⚠️ Wildcard: her olayı yakalar. Yeni olay tipi eklendiğinde binding
        // değişmez — topic exchange'i tam bunun için seçtik.
        await channel.QueueBindAsync(QueueName, options.Value.Exchange,
            routingKey: "nexuserp.*", cancellationToken: ct);

        await channel.BasicQosAsync(0, prefetchCount: 10, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) => await HandleAsync(channel, ea, ct);

        await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer,
                                        cancellationToken: ct);

        logger.LogInformation("Bildirim tüketicisi dinlemede: {Queue} (nexuserp.*)", QueueName);

        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var messageId = ea.BasicProperties.MessageId;
        var type = ea.BasicProperties.Type ?? "";

        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var mail = Compose(type, json);

            // İlgilenmediğimiz olaylar sessizce onaylanır — kuyrukta birikmesin.
            if (mail is null)
            {
                await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
                return;
            }

            // ⚠️ İdempotency: aynı mesaj iki kez gelirse müşteriye İKİ e-posta gider.
            if (!await TryMarkProcessedAsync(db, ea, ct))
            {
                logger.LogDebug("Bildirim zaten gönderilmiş, atlandı: {MessageId}", messageId);
                await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
                return;
            }

            var email = await ResolveEmailAsync(db, mail.PartyId, ct);

            if (email is null)
            {
                logger.LogWarning("Cari {PartyId} için e-posta adresi yok, bildirim atlandı.",
                                  mail.PartyId);
            }
            else
            {
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                await sender.SendAsync(email, mail.Subject, mail.Body, ct);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bildirim işlenemedi: {MessageId} ({Type})", messageId, type);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false,
                                         cancellationToken: ct);
        }
    }

    private sealed record Mail(Guid PartyId, string Subject, string Body);

    /// <summary>
    /// Olayı e-postaya çevirir. İlgilenilmeyen tip için null döner.
    /// Yeni olay eklemek = buraya bir dal eklemek.
    /// </summary>
    private static Mail? Compose(string type, string json) => type switch
    {
        nameof(InvoiceIssued) => FromInvoice(json),
        nameof(SubscriptionPaymentReminder) => FromReminder(json),
        nameof(SubscriptionSuspended) => FromSuspended(json),
        _ => null
    };

    private static Mail? FromInvoice(string json)
    {
        var e = JsonSerializer.Deserialize<InvoiceIssued>(json);
        if (e is null) return null;

        return new Mail(e.PartyId,
            $"Faturanız hazır: {e.Number}",
            $"""
             <p>Sayın {e.PartyTitle},</p>
             <p><b>{e.Number}</b> numaralı faturanız {e.IssueDate:dd.MM.yyyy} tarihinde
             düzenlenmiştir.</p>
             <p>Tutar: <b>{e.GrandTotal:N2} {e.Currency}</b></p>
             <p>İyi çalışmalar dileriz.</p>
             """);
    }

    private static Mail? FromReminder(string json)
    {
        var e = JsonSerializer.Deserialize<SubscriptionPaymentReminder>(json);
        if (e is null) return null;

        // Ton seviyeye göre sertleşiyor — 3. gün nazik, 14. gün uyarı.
        var (subject, opening) = e.Level switch
        {
            1 => ("Ödeme hatırlatması", "Faturanızın vadesi geçmiş görünüyor."),
            2 => ("Ödeme hatırlatması — ikinci bildirim",
                  "Faturanız hâlâ ödenmemiş durumda."),
            _ => ("Önemli: ödenmemiş faturanız var",
                  "Faturanız uzun süredir ödenmemiş durumda.")
        };

        return new Mail(e.PartyId, subject,
            $"""
             <p>Sayın {e.PartyTitle},</p>
             <p>{opening} Gecikme: <b>{e.DaysPastDue} gün</b>.</p>
             <p>Ödenmemiş tutar: <b>{e.OverdueAmount:N2} {e.Currency}</b></p>
             <p>Ödemenizi tamamladıysanız bu bildirimi dikkate almayınız.</p>
             """);
    }

    private static Mail? FromSuspended(string json)
    {
        var e = JsonSerializer.Deserialize<SubscriptionSuspended>(json);
        if (e is null) return null;

        return new Mail(e.PartyId,
            "Aboneliğiniz askıya alındı",
            $"""
             <p>Sayın {e.PartyTitle},</p>
             <p>{e.DaysPastDue} gündür ödenmeyen faturanız nedeniyle aboneliğiniz
             {e.SuspendedOn:dd.MM.yyyy} tarihinde askıya alınmıştır.</p>
             <p>Ödenmemiş tutar: <b>{e.OverdueAmount:N2} {e.Currency}</b></p>
             <p>Ödeme sonrası hizmetiniz yeniden açılacaktır.</p>
             """);
    }

    /// <summary>
    /// ⚠️ IgnoreQueryFilters: tüketici tüm tenant'ların olaylarını işler, tenant
    /// context'i ayarlı değil. Filtre açık olsaydı cari bulunamaz ve hiçbir
    /// bildirim gönderilemezdi.
    /// </summary>
    private static async Task<string?> ResolveEmailAsync(
        AppDbContext db, Guid partyId, CancellationToken ct)
    {
        var email = await db.Parties.IgnoreQueryFilters()
            .Where(p => p.Id == partyId)
            .Select(p => p.Email)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(email) ? null : email;
    }

    private static async Task<bool> TryMarkProcessedAsync(
        AppDbContext db, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        if (!Guid.TryParse(ea.BasicProperties.MessageId, out var messageId))
            return true;

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            ConsumerName = ConsumerName,
            MessageId = messageId,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name == "PostgresException"
           && ex.InnerException.GetType().GetProperty("SqlState")?
                 .GetValue(ex.InnerException) as string == "23505";
}
