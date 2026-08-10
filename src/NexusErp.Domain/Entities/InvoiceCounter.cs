using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Fatura numarası sayacı. (TenantId, Series, Year) başına tek satır.
/// Artırma UPDATE ... RETURNING ile atomik yapılır (ADR-007) —
/// SELECT MAX(no)+1 yarış koşuluna açıktır ve iki fatura aynı numarayı alır.
/// </summary>
public sealed class InvoiceCounter : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public string Series { get; set; } = default!;
    public int Year { get; set; }
    public long LastNumber { get; set; }
}
