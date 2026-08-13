using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusErp.Application.Abstractions;
using RabbitMQ.Client;

namespace NexusErp.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ.Client 7.x — v6'daki IModel / CreateConnection / BasicPublish yok,
/// hepsi async ve IChannel oldu.
///
/// ⚠️ SINGLETON kaydedilir. Bağlantı kurmak pahalıdır (TCP + AMQP handshake);
/// her mesajda yeniden açarsan saniyede birkaç mesajda tıkanırsın.
/// </summary>
public sealed class RabbitMqEventPublisher(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqEventPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;

        // ⚠️ IChannel THREAD-SAFE DEĞİL ve kurulum yarışa açık.
        // Semaphore olmadan iki eşzamanlı yayın iki kanal açar, biri sızar.
        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Value.Uri),
                ClientProvidedName = "nexuserp-outbox",
                AutomaticRecoveryEnabled = true   // ağ koparsa kendi toparlasın
            };

            _connection = await factory.CreateConnectionAsync(ct);

            // ⚠️ Publisher confirms AÇIK. Bu olmadan BasicPublishAsync mesajı sokete
            // yazar yazmaz döner — broker'a ulaşıp ulaşmadığını BİLMEZSİN. İşçi de
            // "yayınlandı" sanıp ProcessedAt'i doldurur ve mesaj sessizce kaybolur.
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true,
                                         publisherConfirmationTrackingEnabled: true),
                ct);

            // mandatory:true ile yönlenemeyen mesaj geri döner — dinlemezsek
            // sessizce düşer, bu yüzden loglanıyor.
            _channel.BasicReturnAsync += (_, ea) =>
            {
                logger.LogError("Mesaj hiçbir kuyruğa yönlenemedi: {RoutingKey} ({Reply})",
                                ea.RoutingKey, ea.ReplyText);
                return Task.CompletedTask;
            };

            await _channel.ExchangeDeclareAsync(
                exchange: options.Value.Exchange,
                type: ExchangeType.Topic,
                durable: true,          // broker yeniden başlayınca kaybolmasın
                autoDelete: false,
                cancellationToken: ct);

            await _channel.ExchangeDeclareAsync(
                exchange: options.Value.DeadLetterExchange,
                type: ExchangeType.Fanout,
                durable: true, autoDelete: false, cancellationToken: ct);

            logger.LogInformation("RabbitMQ bağlantısı kuruldu: {Exchange}", options.Value.Exchange);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PublishAsync(string type, string payload, Guid messageId,
                                   Guid tenantId, CancellationToken ct = default)
    {
        var channel = await GetChannelAsync(ct);

        var props = new BasicProperties
        {
            MessageId = messageId.ToString(),
            Type = type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,   // broker restart'ında hayatta kalsın
            Headers = new Dictionary<string, object?> { ["tenant-id"] = tenantId.ToString() }
        };

        await channel.BasicPublishAsync(
            exchange: options.Value.Exchange,
            routingKey: $"nexuserp.{type}",
            mandatory: true,
            basicProperties: props,
            body: Encoding.UTF8.GetBytes(payload),
            cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
