using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Bir tüketicinin işlediği mesajların kaydı — "en az bir kez" teslimin
/// karşılığı olan idempotency defteri.
///
/// Outbox işçisi mesajı yayınlayıp <c>ProcessedAt</c>'i yazamadan çökerse aynı
/// mesaj tekrar teslim edilir. Yan etkisi olan tüketiciler (e-posta, muhasebe
/// fişi) bunu tolere etmek zorunda: işlemeden önce buraya yazmayı dener,
/// <c>(consumer_name, message_id)</c> unique index'i ihlal olursa mesaj zaten
/// işlenmiştir ve atlanır.
///
/// ⚠️ ITenantScoped DEĞİL. Tüketici tüm tenant'ların mesajlarını işler; tenant
/// filtresi açık olsaydı başka tenant'ın kaydını göremez, aynı mesajı ikinci kez
/// işlerdi. TenantId yalnızca tanı amaçlı saklanıyor.
/// </summary>
public sealed class ProcessedMessage : AuditableEntity
{
    /// <summary>Hangi tüketici. Aynı mesajı farklı tüketiciler ayrı ayrı işler.</summary>
    public string ConsumerName { get; set; } = default!;

    /// <summary>Outbox satırının Id'si — RabbitMQ'da MessageId olarak taşınıyor.</summary>
    public Guid MessageId { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
