using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

/// <summary>Tahsilatın hangi faturaya ne kadar sayıldığı (mutabakat kaydı).</summary>
public sealed class PaymentAllocation : AuditableEntity
{
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = default!;

    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = default!;

    public decimal Amount { get; set; }
    public DateOnly AllocatedOn { get; set; }
}
