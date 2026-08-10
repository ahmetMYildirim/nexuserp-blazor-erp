using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

/// <summary>
/// KDV oranı. Enum DEĞİL — oranlar mevzuatla değişiyor (2023'te %18 → %20 oldu).
/// ValidFrom/ValidTo ile tarihsel geçerlilik tutulur; fatura kesilirken oran faturaya
/// KOPYALANIR ki sonradan oran değişince eski faturalar bozulmasın.
/// </summary>
public sealed class TaxRate : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = default!;      // "KDV %20"
    public string Code { get; set; } = default!;      // "KDV20"

    /// <summary>Oran, ondalık: %20 → 0,20</summary>
    public decimal Rate { get; set; }

    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public bool IsDefault { get; set; }

    public bool IsValidOn(DateOnly date) =>
        date >= ValidFrom && (ValidTo is null || date <= ValidTo);
}
