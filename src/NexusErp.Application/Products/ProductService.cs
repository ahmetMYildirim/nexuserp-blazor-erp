using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Parties;

namespace NexusErp.Application.Products;

public sealed class ProductService(IAppDbContext db)
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<PagedResult<ProductListItem>> SearchAsync(
        ProductQuery q, CancellationToken ct = default)
    {
        var query = db.Products.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + q.Search.Trim().ToUpper(Tr) + "%";
            query = query.Where(p =>
                EF.Functions.Like(p.Code.ToUpper(), pattern) ||
                EF.Functions.Like(p.Name.ToUpper(), pattern));
        }

        if (q.Kind is not null)
            query = query.Where(p => p.Kind == q.Kind.Value);

        if (q.IsActive is not null)
            query = query.Where(p => p.IsActive == q.IsActive.Value);

        var total = await query.CountAsync(ct);

        query = (q.SortBy, q.Descending) switch
        {
            (nameof(ProductListItem.Name), false) => query.OrderBy(p => p.Name),
            (nameof(ProductListItem.Name), true) => query.OrderByDescending(p => p.Name),
            (nameof(ProductListItem.UnitPrice), false) => query.OrderBy(p => p.UnitPrice),
            (nameof(ProductListItem.UnitPrice), true) => query.OrderByDescending(p => p.UnitPrice),
            (_, true) => query.OrderByDescending(p => p.Code),
            _ => query.OrderBy(p => p.Code)
        };

        // ⚠️ Include YOK: Select projeksiyonu zaten gereken kolonları JOIN ile çekiyor.
        // Include eklersek tüm TaxRate entity'si de materialize edilir — boşuna iş.
        var items = await query
            .Skip(q.Page * q.PageSize)
            .Take(q.PageSize)
            .Select(p => new ProductListItem(
                p.Id, p.Code, p.Name, p.Kind, p.Unit, p.UnitPrice, p.Currency,
                p.TaxRate.Name, p.TaxRate.Rate, p.WithholdingRate, p.IsActive))
            .ToListAsync(ct);

        return new PagedResult<ProductListItem>(items, total);
    }

    /// <summary>Fatura satırındaki otomatik tamamlama için — 20 sonuç yeter.</summary>
    public async Task<IReadOnlyList<ProductLookupItem>> LookupAsync(
        string? term, CancellationToken ct = default)
    {
        var query = db.Products.AsNoTracking().Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(term))
        {
            var pattern = "%" + term.Trim().ToUpper(Tr) + "%";
            query = query.Where(p =>
                EF.Functions.Like(p.Code.ToUpper(), pattern) ||
                EF.Functions.Like(p.Name.ToUpper(), pattern));
        }

        return await query
            .OrderBy(p => p.Code)
            .Take(20)
            .Select(p => new ProductLookupItem(
                p.Id, p.Code, p.Name, p.Unit, p.UnitPrice,
                p.TaxRateId, p.TaxRate.Rate, p.WithholdingRate))
            .ToListAsync(ct);
    }
}
