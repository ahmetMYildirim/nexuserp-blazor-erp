using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

public sealed class Product : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public string Code { get; set; } = default!;       // "URN0001"
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public ProductKind Kind { get; set; } = ProductKind.Service;

    /// <summary>Birim: Adet, Kg, Saat, Ay...</summary>
    public string Unit { get; set; } = "Adet";

    /// <summary>KDV hariç liste fiyatı.</summary>
    public decimal UnitPrice { get; set; }
    public string Currency { get; set; } = "TRY";

    public Guid TaxRateId { get; set; }
    public TaxRate TaxRate { get; set; } = default!;

    /// <summary>
    /// Tevkifat oranı, ondalık. 7/10 tevkifat → 0,70. Null = tevkifat yok.
    /// Ürün kartında varsayılan; faturada satır bazında geçersiz kılınabilir.
    /// </summary>
    public decimal? WithholdingRate { get; set; }

    public bool IsActive { get; set; } = true;

    public void EnsureCanBeSold()
    {
        if (!IsActive)
            throw new DomainException($"'{Name}' pasif durumda, faturaya eklenemez.");
        if (UnitPrice < 0)
            throw new DomainException("Birim fiyat negatif olamaz.");
    }
}
