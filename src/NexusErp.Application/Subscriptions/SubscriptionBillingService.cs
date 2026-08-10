using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Subscriptions;

namespace NexusErp.Application.Subscriptions;

public sealed class SubscriptionBillingService(
    IAppDbContext db,
    InvoiceService invoices,
    ILogger<SubscriptionBillingService> logger)
{
    /// <summary>
    /// Vadesi gelen abonelikleri faturalandırır.
    /// Bir aboneliğin hatası diğerlerini ENGELLEMEZ.
    /// </summary>
    public async Task<BillingRunResult> RunAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var due = await db.Subscriptions
            .Include(s => s.Plan).ThenInclude(p => p.Product).ThenInclude(p => p.TaxRate)
            .Include(s => s.Party)
            .Where(s => (s.Status == SubscriptionStatus.Active
                      || s.Status == SubscriptionStatus.PastDue)
                     && s.NextBillingDate <= asOf
                     && (s.EndDate == null || s.EndDate >= asOf))
            .ToListAsync(ct);

        int created = 0, skipped = 0, failed = 0;

        foreach (var sub in due)
        {
            try
            {
                if (await BillOneAsync(sub, ct)) created++;
                else skipped++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Abonelik {SubscriptionId} faturalandırılamadı.", sub.Id);
            }
        }

        logger.LogInformation(
            "Abonelik faturalandırma tamamlandı ({AsOf}): {Created} yeni, {Skipped} atlandı, {Failed} hata.",
            asOf, created, skipped, failed);

        return new BillingRunResult(created, skipped, failed);
    }

    private async Task<bool> BillOneAsync(Subscription sub, CancellationToken ct)
    {
        var periodStart = sub.NextBillingDate;
        var periodEnd = BillingSchedule.PeriodEnd(periodStart, sub.Plan.Cycle, sub.BillingAnchorDay);

        // Ön kontrol PERFORMANS için; GARANTİ veri tabanındaki
        // (subscription_id, period_start) unique index'ten geliyor.
        // İş mantığındaki kontrol yarış koşulunda yanılır, DB kısıtı yanılmaz.
        var alreadyBilled = await db.Invoices
            .AnyAsync(i => i.SubscriptionId == sub.Id && i.PeriodStart == periodStart, ct);

        if (alreadyBilled)
        {
            AdvanceSchedule(sub, periodStart);
            await db.SaveChangesAsync(ct);
            return false;
        }

        var product = sub.Plan.Product;
        var unitPrice = sub.CustomPrice ?? sub.Plan.Price;

        var form = new InvoiceForm
        {
            PartyId = sub.PartyId,
            Type = InvoiceType.Sales,
            Series = "ABN",                       // abonelik faturaları ayrı seri
            IssueDate = periodStart,
            Currency = sub.Plan.Currency,
            SubscriptionId = sub.Id,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Notes = $"Abonelik dönemi: {periodStart:dd.MM.yyyy} – {periodEnd:dd.MM.yyyy}",
            Lines =
            [
                new InvoiceLineForm
                {
                    ProductId = product.Id,
                    ProductCode = product.Code,
                    ProductName = $"{sub.Plan.Name} ({periodStart:dd.MM.yyyy}–{periodEnd:dd.MM.yyyy})",
                    Unit = product.Unit,
                    Quantity = sub.Quantity,
                    UnitPrice = unitPrice,
                    TaxRateId = product.TaxRateId,
                    TaxRate = product.TaxRate.Rate,
                    WithholdingRate = product.WithholdingRate
                }
            ]
        };

        try
        {
            var invoiceId = await invoices.SaveDraftAsync(form, ct);
            await invoices.IssueAsync(invoiceId, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Yarış koşulu: başka bir çalışma aynı dönemi faturalamış.
            // Bu bir HATA DEĞİL — idempotency'nin çalıştığının kanıtı.
            logger.LogWarning("Abonelik {Id} / {Period} zaten faturalanmış, atlandı.",
                              sub.Id, periodStart);
            AdvanceSchedule(sub, periodStart);
            await db.SaveChangesAsync(ct);
            return false;
        }

        AdvanceSchedule(sub, periodStart);
        if (sub.Status == SubscriptionStatus.Trialing) sub.Status = SubscriptionStatus.Active;

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static void AdvanceSchedule(Subscription sub, DateOnly billedPeriodStart)
        => sub.NextBillingDate = BillingSchedule.NextPeriodStart(
               billedPeriodStart, sub.Plan.Cycle, sub.BillingAnchorDay);

    /// <summary>
    /// PostgreSQL 23505 = unique_violation.
    /// ⚠️ Npgsql tipine Application katmanından bakmak Clean Architecture'a küçük bir
    /// sızıntı. Bilinçli ödün: alternatifi IDbExceptionTranslator arayüzü yazmak.
    /// Faz 2 teknik borç listesinde.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name == "PostgresException"
           && ex.InnerException.GetType().GetProperty("SqlState")?
                 .GetValue(ex.InnerException) as string == "23505";
}
