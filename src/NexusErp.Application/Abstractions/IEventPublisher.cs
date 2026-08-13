namespace NexusErp.Application.Abstractions;

/// <summary>
/// Entegrasyon olaylarını dış dünyaya yayınlar. Application katmanı RabbitMQ'yu
/// bilmez; gerçeklemesi Infrastructure'da.
/// </summary>
public interface IEventPublisher
{
    /// <param name="messageId">
    /// Outbox satırının Id'si. Tüketici idempotency'sinin dayanağı — aynı mesaj
    /// iki kez teslim edilirse tüketici bu kimlikten anlar.
    /// </param>
    Task PublishAsync(string type, string payload, Guid messageId,
                      Guid tenantId, CancellationToken ct = default);
}
