using NexusErp.Domain.Enums;

namespace NexusErp.Application.Invoicing;

public sealed record InvoiceListItem(
    Guid Id,
    string? Number,
    InvoiceType Type,
    InvoiceStatus Status,
    string PartyTitle,
    DateOnly IssueDate,
    DateOnly DueDate,
    string Currency,
    decimal GrandTotal,
    decimal PaidAmount,
    Guid? SubscriptionId)
{
    public decimal RemainingAmount => GrandTotal - PaidAmount;

    public bool IsOverdue =>
        Status is InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid &&
        DueDate < DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Alışta borç bizde — listede tutarı ters işaretle göstermek için.</summary>
    public bool IsPurchase => Type == InvoiceType.Purchase;
}

public sealed class InvoiceLineForm
{
    public Guid? ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Unit { get; set; } = "Adet";
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public DiscountType DiscountType { get; set; } = DiscountType.None;
    public decimal DiscountValue { get; set; }
    public Guid? TaxRateId { get; set; }
    public decimal TaxRate { get; set; }
    public decimal? WithholdingRate { get; set; }
    public string? Description { get; set; }
}

public sealed class InvoiceForm
{
    public Guid? Id { get; set; }
    public Guid PartyId { get; set; }
    public InvoiceType Type { get; set; } = InvoiceType.Sales;
    public string Series { get; set; } = "NEX";
    public DateOnly IssueDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    /// <summary>Null ise carinin ödeme vadesinden hesaplanır.</summary>
    public DateOnly? DueDate { get; set; }
    public string Currency { get; set; } = "TRY";
    public decimal ExchangeRate { get; set; } = 1m;
    public DiscountType DocumentDiscountType { get; set; } = DiscountType.None;
    public decimal DocumentDiscountValue { get; set; }
    public string? Notes { get; set; }

    /// <summary>Alış faturasında ZORUNLU: tedarikçinin kendi fatura numarası.</summary>
    public string? SupplierInvoiceNo { get; set; }

    // Abonelik faturalandırması doldurur (Bölüm 09)
    public Guid? SubscriptionId { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }

    public List<InvoiceLineForm> Lines { get; set; } = [];
}

public sealed record InvoiceQuery(
    string? Search = null,
    InvoiceStatus? Status = null,
    InvoiceType? Type = null,
    Guid? PartyId = null,
    bool OnlyOverdue = false,
    int Page = 0,
    int PageSize = 25,
    string SortBy = nameof(InvoiceListItem.IssueDate),
    bool Descending = true);
