using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Sistemi kullanan firma. ITenantScoped implemente ETMEZ — kendisi kapsamın sahibi.
/// </summary>
public sealed class Tenant : AuditableEntity
{
    public string Name { get; set; } = default!;
    public string TaxNumber { get; set; } = default!;
    public string? TaxOffice { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    /// <summary>Fatura numarası seri kodu, 3 harf. "NEX" → NEX2026000000001</summary>
    public string InvoiceSeries { get; set; } = "NEX";

    public string DefaultCurrency { get; set; } = "TRY";
    public bool IsActive { get; set; } = true;
}
