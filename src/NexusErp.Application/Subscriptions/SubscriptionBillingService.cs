using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Subscriptions;

namespace NexusErp.Application.Subscriptions;

public sealed class SubscriptionBillingService(
    IAppDbContextFactory factory,
    InvoiceService invoices,
    ILogger<SubscriptionBillingService> logger)
{
    /// <summary>
    /// Vadesi gelen abonelikleri faturalandırır.
    /// Bir aboneliğin hatası diğerlerini ENGELLEMEZ.
    /// </summary>
    /// <summary>
    /// Turda ne olacağını gösterir, HİÇBİR ŞEY KAYDETMEZ.
    ///
    /// Seçim koşulu <see cref="RunAsync"/> ile birebir aynı olmak zorunda —
    /// ayrışırsa önizleme yalan söyler ve kullanıcı beklemediği fatura görür.
    /// </summary>
    public async Task<BillingRunPreview> PreviewRunAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var due = await db.Subscriptions
            .AsNoTracking()
            .Include(s => s.Plan)
            .Include(s => s.Party)
            .Where(s => (s.Status == SubscriptionStatus.Active
                      || s.Status == SubscriptionStatus.PastDue)
                     && s.NextBillingDate <= asOf
                     && (s.EndDate == null || s.EndDate >= asOf))
            .OrderBy(s => s.NextBillingDate)
            .ToListAsync(ct);

        var rows = new List<BillingPreviewRow>(due.Count);

        foreach (var sub in due)
        {
            var periodStart = sub.NextBillingDate;
            var periodEnd = BillingSchedule.PeriodEnd(
                periodStart, sub.Plan.Cycle, sub.BillingAnchorDay);

            // Aynı idempotency kontrolü — bu dönem zaten faturalanmış mı?
            var alreadyBilled = await db.Invoices
                .AnyAsync(i => i.SubscriptionId == sub.Id && i.PeriodStart == periodStart, ct);

            var unitPrice = sub.CustomPrice ?? sub.Plan.Price;

            rows.Add(new BillingPreviewRow(
                SubscriptionId: sub.Id,
                PartyTitle: sub.Party.Title,
                PlanName: sub.Plan.Name,
                PeriodStart: periodStart,
                PeriodEnd: periodEnd,
                Amount: Math.Round(unitPrice * sub.Quantity, 2, MidpointRounding.AwayFromZero),
                Currency: sub.Plan.Currency,
                AlreadyBilled: alreadyBilled));
        }

        return new BillingRunPreview(asOf, rows);
    }

    public async Task<BillingRunResult> RunAsync(DateOnly asOf, CancellationToken ct = default)
    {
        await using var db = factory.Create();

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
                if (await BillOneAsync(db, sub, ct)) created++;
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

    private async Task<bool> BillOneAsync(IAppDbContext db, Subscription sub, CancellationToken ct)
    {
        // ⚠️ invoices.SaveDraftAsync KENDİ context'ini açıyor — abonelik ve fatura
        // FARKLI transaction'larda. Fatura yazılıp AdvanceSchedule başarısız olursa
        // abonelik geride kalır; sonraki tur unique index'e takılır, atlar ve tarihi
        // ilerletir. Kendini onarır. Tek transaction gerekseydi paylaşılan context
        // veya TransactionScope kullanmak gerekirdi.
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
