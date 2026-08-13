using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;
using NexusErp.Domain.ValueObjects;

namespace NexusErp.Domain.Entities;

/// <summary>Cari kart — müşteri ve/veya tedarikçi.</summary>
public sealed class Party : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    /// <summary>Cari kod — kullanıcı tarafından verilen kısa kod. "MUS0001"</summary>
    public string Code { get; set; } = default!;

    public string Title { get; set; } = default!;          // ticari unvan
    public PartyType Type { get; set; } = PartyType.Customer;

    // --- Vergi bilgileri ---
    public string? TaxNumber { get; set; }                 // VKN veya TCKN
    public TaxIdKind? TaxNumberKind { get; set; }
    public string? TaxOffice { get; set; }

    // --- İletişim ---
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }

    // --- Ticari koşullar ---
    /// <summary>Ödeme vadesi (gün). Fatura tarihine eklenerek vade tarihi bulunur.</summary>
    public int PaymentTermDays { get; set; } = 30;

    /// <summary>Kredi limiti. 0 = limitsiz.</summary>
    public decimal CreditLimit { get; set; }

    public string Currency { get; set; } = "TRY";
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    // ------------------------------------------------------------------
    // İş kuralları — entity kendi tutarlılığını korur (anemik model değil)
    // ------------------------------------------------------------------

    /// <summary>
    /// VKN/TCKN doğrulaması tek noktada. Property'ye doğrudan yazmak yerine bunu kullan.
    /// </summary>
    public void SetTaxNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            TaxNumber = null;
            TaxNumberKind = null;
            return;
        }

        if (!TaxIdentifier.TryParse(raw, out var id))
            throw new DomainException(
                "VKN 10, TCKN 11 haneli olmalı ve kontrol basamağı doğru olmalı.");

        TaxNumber = id.Value;
        TaxNumberKind = id.Kind;
    }

    public bool IsCustomer => Type.HasFlag(PartyType.Customer);
    public bool IsSupplier => Type.HasFlag(PartyType.Supplier);

    /// <summary>Vade tarihi. Fatura entity'sinden bağımsız test edilebilir.</summary>
    public DateOnly CalculateDueDate(DateOnly invoiceDate) => invoiceDate.AddDays(PaymentTermDays);

    /// <summary>Alış faturası kesilebilmesi için cari tedarikçi olmalı.</summary>
    public void EnsureCanBePurchasedFrom()
    {
        if (!IsActive)
            throw new DomainException($"'{Title}' pasif durumda, alış faturası girilemez.");
        if (!IsSupplier)
            throw new DomainException($"'{Title}' tedarikçi tipinde değil, alış faturası girilemez.");
    }

    public void EnsureCanBeInvoiced()
    {
        if (!IsActive)
            throw new DomainException($"'{Title}' pasif durumda, fatura kesilemez.");
        if (!IsCustomer)
            throw new DomainException($"'{Title}' müşteri tipinde değil, satış faturası kesilemez.");
    }
}
