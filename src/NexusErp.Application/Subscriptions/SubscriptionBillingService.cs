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

            var flat = sub.Plan.HasFlatFee
                ? Math.Round(unitPrice * sub.Quantity, 2, MidpointRounding.AwayFromZero)
                : 0m;

            // ⚠️ Kullanım hesabı RunAsync ile AYNI yardımcıdan geçiyor. İki yerde
            // ayrı ayrı yazılsaydı önizleme ile gerçek fatura zamanla ayrışırdı —
            // kullanıcı gördüğünden farklı bir fatura kesilirdi.
            var usage = sub.Plan.IsMetered
                ? await CalculateUsageAsync(db, sub, periodStart, ct)
                : null;

            rows.Add(new BillingPreviewRow(
                SubscriptionId: sub.Id,
                PartyTitle: sub.Party.Title,
                PlanName: sub.Plan.Name,
                PeriodStart: periodStart,
                PeriodEnd: periodEnd,
                Amount: flat + (usage?.Amount ?? 0m),
                Currency: sub.Plan.Currency,
                AlreadyBilled: alreadyBilled,
                UsageQuantity: usage?.BillableUnits ?? 0m,
                UsageAmount: usage?.Amount ?? 0m,
                UsageUnitName: sub.Plan.UsageUnitName));
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

        var lines = new List<InvoiceLineForm>();

        if (sub.Plan.HasFlatFee)
        {
            lines.Add(new InvoiceLineForm
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
            });
        }

        // ⚠️ Kullanım satırı GEÇMİŞ döneme aittir, sabit ücret satırı GELECEK döneme.
        // Aynı faturada iki farklı dönem görünmesi hata değil, zorunluluk: bir dönemin
        // kullanımı ancak dönem bittiğinde bilinir.
        UsageCharge? usage = null;

        if (sub.Plan.IsMetered)
        {
            usage = await CalculateUsageAsync(db, sub, periodStart, ct);

            if (usage.BillableUnits > 0)
            {
                lines.Add(new InvoiceLineForm
                {
                    ProductId = product.Id,
                    ProductCode = product.Code,
                    ProductName = $"{sub.Plan.Name} kullanım " +
                                  $"({usage.From:dd.MM.yyyy}–{usage.To:dd.MM.yyyy})",
                    Unit = sub.Plan.UsageUnitName ?? "Birim",
                    Quantity = usage.BillableUnits,
                    UnitPrice = sub.Plan.OveragePrice,
                    TaxRateId = product.TaxRateId,
                    TaxRate = product.TaxRate.Rate,
                    WithholdingRate = product.WithholdingRate
                });
            }
        }

        // Saf kullanım planında kullanım yoksa fatura da YOK. Sıfır tutarlı fatura
        // kesmek hem mevzuata aykırı hem de müşteriyi gereksiz yere rahatsız eder.
        if (lines.Count == 0)
        {
            logger.LogInformation(
                "Abonelik {Id} / {Period}: faturalanacak kullanım yok, dönem atlandı.",
                sub.Id, periodStart);
            AdvanceSchedule(sub, periodStart);
            await db.SaveChangesAsync(ct);
            return false;
        }

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
            Lines = lines
        };

        try
        {
            var invoiceId = await invoices.SaveDraftAsync(form, ct);

            // ⚠️ Damgalama KESMEDEN ÖNCE: kesme başarısız olursa kullanım kayıtları
            // taslak faturaya bağlı kalır ve bir daha faturalanmaz — mükerrer tahsilat
            // riski yok. Ters sırada yapsaydık damgalama hatası kullanımı ikinci kez
            // faturalardı; iki kötü senaryodan az zararlısı bu.
            if (usage is not null && usage.RecordIds.Count > 0)
                await StampUsageAsync(db, usage.RecordIds, invoiceId, ct);

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

    /// <summary>Faturalanacak kullanım. Salt hesap — hiçbir şey yazmaz.</summary>
    private sealed record UsageCharge(
        DateOnly From, DateOnly To, decimal RawUnits, decimal Allowance,
        decimal BillableUnits, decimal Amount, IReadOnlyList<Guid> RecordIds);

    /// <summary>
    /// Faturalanmamış TÜM kullanımı toplar (tarihe göre değil, DAMGAYA göre).
    ///
    /// ⚠️ "Şu dönemin kayıtları" diye sorsaydık geç gelen kayıtlar sonsuza kadar
    /// faturalanmadan kalırdı: entegrasyon bir gün geç veri gönderdiğinde o kullanım
    /// hiçbir faturaya girmezdi. Damga (invoice_id) tek doğruluk kaynağı.
    ///
    /// ⚠️ Ücretsiz kota fatura başına BİR KEZ düşülür. Geç gelen eski dönem kayıtları
    /// bu faturanın kotasını paylaşır; alternatifi kapanmış bir faturayı yeniden
    /// açmaktır ki o çok daha kötüdür.
    /// </summary>
    private static async Task<UsageCharge> CalculateUsageAsync(
        IAppDbContext db, Subscription sub, DateOnly periodStart, CancellationToken ct)
    {
        // periodStart = peşin ücretin başladığı gün ⇒ ondan öncesi GEÇMİŞTİR.
        var records = await db.UsageRecords.AsNoTracking()
            .Where(u => u.SubscriptionId == sub.Id
                     && u.InvoiceId == null
                     && u.OccurredOn < periodStart)
            .Select(u => new { u.Id, u.OccurredOn, u.Quantity })
            .ToListAsync(ct);

        var from = BillingSchedule.PreviousPeriodStart(
            periodStart, sub.Plan.Cycle, sub.BillingAnchorDay);
        var to = periodStart.AddDays(-1);

        if (records.Count == 0)
            return new UsageCharge(from, to, 0m, 0m, 0m, 0m, []);

        // Geç gelen kayıt varsa dönem etiketini geriye doğru genişlet — faturada
        // yazan aralık gerçekten kapsanan aralık olsun.
        var earliest = records.Min(r => r.OccurredOn);
        if (earliest < from) from = earliest;

        var raw = records.Sum(r => r.Quantity);
        var allowance = sub.Plan.AllowanceFor(sub.Quantity);
        var billable = Math.Max(0m, raw - allowance);

        return new UsageCharge(
            From: from,
            To: to,
            RawUnits: raw,
            Allowance: allowance,
            BillableUnits: billable,
            Amount: Math.Round(billable * sub.Plan.OveragePrice, 2, MidpointRounding.AwayFromZero),
            RecordIds: [.. records.Select(r => r.Id)]);
    }

    /// <summary>
    /// Kullanım kayıtlarını faturaya damgalar.
    /// ⚠️ Kota içinde kalıp ücretlendirilmeyen kayıtlar da damgalanır — yoksa
    /// bir sonraki turda tekrar toplanır ve kotayı ikinci kez tüketirler.
    /// </summary>
    private static async Task StampUsageAsync(
        IAppDbContext db, IReadOnlyList<Guid> ids, Guid invoiceId, CancellationToken ct)
    {
        var records = await db.UsageRecords
            .Where(u => ids.Contains(u.Id) && u.InvoiceId == null)
            .ToListAsync(ct);

        foreach (var record in records)
            record.MarkBilled(invoiceId);

        await db.SaveChangesAsync(ct);
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
