using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

public sealed class OutBoxMessage : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    // Olay tipi: "InvoiceIssued". Routing key buna göre üretilir
    public string Type { get; set; } = default!;

    public string Payload { get; set; } = default!;

    public DateTimeOffset OccuredAt { get; set; }

    /// <summary>
    /// Dolduysa yayınlandı. NULL olanlar işçinin iş listesi.
    /// ⚠️ NULLABLE olmak ZORUNDA — tüm mekanizma "processed_at IS NULL"
    /// koşuluna dayanıyor; partial index de bu filtreyle kuruluyor.
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

}
