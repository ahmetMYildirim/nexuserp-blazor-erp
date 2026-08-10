namespace NexusErp.Domain.Common;

public abstract class AuditableEntity
{
    /// <summary>
    /// UUIDv7 — ilk 48 bit zaman damgası, yani zaman sıralı. Rastgele v4 GUID birincil
    /// anahtar olarak kullanıldığında B-tree index'te sayfa bölünmesine yol açar;
    /// v7 sıralı olduğu için int identity kadar hızlı index'lenir ama dağıtık üretilebilir.
    /// </summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "system";
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Muhasebe verisi silinmez (ADR-009). Remove() çağrısı bunu true yapar.</summary>
    public bool IsDeleted { get; set; }
}
