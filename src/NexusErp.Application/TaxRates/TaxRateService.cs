using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;

namespace NexusErp.Application.TaxRates;

public sealed record TaxRateItem(
    Guid Id, string Code, string Name, decimal Rate, bool IsDefault,
    DateOnly ValidFrom, DateOnly? ValidTo);

public sealed class TaxRateService(IAppDbContext db)
{
    /// <summary>Belirtilen tarihte geçerli olan oranlar (fatura satırı dropdown'ı için).</summary>
    public Task<List<TaxRateItem>> GetValidOnAsync(DateOnly date, CancellationToken ct = default) =>
        db.TaxRates.AsNoTracking()
          .Where(t => t.ValidFrom <= date && (t.ValidTo == null || t.ValidTo >= date))
          .OrderByDescending(t => t.Rate)
          .Select(t => new TaxRateItem(t.Id, t.Code, t.Name, t.Rate, t.IsDefault,
                                       t.ValidFrom, t.ValidTo))
          .ToListAsync(ct);

    public async Task<TaxRateItem> GetDefaultAsync(DateOnly date, CancellationToken ct = default)
    {
        var rate = await db.TaxRates.AsNoTracking()
            .Where(t => t.IsDefault && t.ValidFrom <= date
                        && (t.ValidTo == null || t.ValidTo >= date))
            .Select(t => new TaxRateItem(t.Id, t.Code, t.Name, t.Rate, t.IsDefault,
                                         t.ValidFrom, t.ValidTo))
            .FirstOrDefaultAsync(ct);

        return rate ?? throw new DomainException(
            "Geçerli varsayılan KDV oranı tanımlı değil.");
    }
}
