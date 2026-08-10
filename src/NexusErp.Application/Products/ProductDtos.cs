using NexusErp.Domain.Enums;

namespace NexusErp.Application.Products;

public sealed record ProductListItem(
    Guid Id,
    string Code,
    string Name,
    ProductKind Kind,
    string Unit,
    decimal UnitPrice,
    string Currency,
    string TaxRateName,
    decimal TaxRate,
    decimal? WithholdingRate,
    bool IsActive);

/// <summary>Fatura satırı seçicisi için hafif model (Bölüm 08).</summary>
public sealed record ProductLookupItem(
    Guid Id,
    string Code,
    string Name,
    string Unit,
    decimal UnitPrice,
    Guid TaxRateId,
    decimal TaxRate,
    decimal? WithholdingRate);

public sealed record ProductQuery(
    string? Search = null,
    ProductKind? Kind = null,
    bool? IsActive = true,
    int Page = 0,
    int PageSize = 25,
    string SortBy = nameof(ProductListItem.Code),
    bool Descending = false);
