using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Parties;
using NexusErp.Application.Events;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Subscriptions;

namespace NexusErp.Application.Subscriptions;

public sealed class SubscriptionService(IAppDbContextFactory factory)
{
    public async Task<List<PlanListItem>> GetPlansAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await db.Plans.AsNoTracking()
            .OrderBy(p => p.Price)
            .Select(p => new PlanListItem(
                p.Id, p.Code, p.Name, p.Price, p.Currency, p.Cycle, p.TrialDays, p.IsActive,
                db.Subscriptions.Count(s => s.PlanId == p.Id
                                         && s.Status == SubscriptionStatus.Active),
                // ⚠️ Saf kullanım planı MRR'a katkı vermez: tutar her ay değişir,
                // taahhüt edilmiş yinelenen gelir yoktur.
                p.BillingModel == BillingModel.Metered ? 0m : p.Price / (int)p.Cycle,
                p.BillingModel, p.UsageUnitName, p.IncludedUnits, p.OveragePrice))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<SubscriptionListItem>> SearchAsync(
        SubscriptionStatus? status = null, int page = 0, int pageSize = 25,
        CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var query = db.Subscriptions.AsNoTracking();
        if (status is not null) query = query.Where(s => s.Status == status.Value);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(s => s.NextBillingDate)
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(s => new SubscriptionListItem(
                s.Id, s.Party.Title, s.Plan.Name, s.Status, s.StartDate, s.NextBillingDate,
                s.BillingAnchorDay, s.Plan.Cycle,
                s.CustomPrice ?? s.Plan.Price, s.Plan.Currency, s.Quantity))
            .ToListAsync(ct);

        return new PagedResult<SubscriptionListItem>(items, total);
    }

    /// <summary>
    /// MRR: tüm aktif aboneliklerin dönem ücreti AYA NORMALİZE edilip toplanır.
    /// Yıllık plan 12'ye bölünür — bölmezsen rakamlar 12 kat şişer.
    /// </summary>
    public async Task<SubscriptionStats> GetStatsAsync(DateOnly today, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var rows = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active
                     || s.Status == SubscriptionStatus.PastDue)
            .Select(s => new
            {
                s.Status,
                s.Quantity,
                Price = s.CustomPrice ?? s.Plan.Price,
                Months = (int)s.Plan.Cycle,
                s.NextBillingDate
            })
            .ToListAsync(ct);

        var active = rows.Where(r => r.Status == SubscriptionStatus.Active).ToList();
        var mrr = Math.Round(active.Sum(a => a.Price * a.Quantity / a.Months),
                             2, MidpointRounding.AwayFromZero);

        return new SubscriptionStats(
            ActiveCount: active.Count,
            Mrr: mrr,
            Arr: Math.Round(mrr * 12m, 2, MidpointRounding.AwayFromZero),
            RenewingThisMonth: active.Count(a => a.NextBillingDate.Month == today.Month
                                              && a.NextBillingDate.Year == today.Year),
            PastDueCount: rows.Count(r => r.Status == SubscriptionStatus.PastDue));
    }

    public async Task CancelAsync(Guid id, DateOnly on, bool immediately,
                                  CancellationReason reason = CancellationReason.Unspecified,
                                  string? note = null,
                                  CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, ct)
                  ?? throw new DomainException("Abonelik bulunamadı.");

        sub.Cancel(on, immediately, reason, note);

        db.AddEvent(new SubscriptionCancelled(sub.Id, sub.PartyId, on, immediately),
                    DateTimeOffset.UtcNow);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Verilen aralıkta iptal edilen abonelikleri sebebe göre gruplar.
    /// Kayıp MRR aya normalize edilir — yıllık plan 12'ye bölünür, aksi halde
    /// kayıp 12 kat şişik görünür.
    /// </summary>
    public async Task<ChurnAnalysis> GetChurnAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var rows = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Cancelled
                     && s.CancelledOn != null
                     && s.CancelledOn >= from && s.CancelledOn <= to)
            .Select(s => new
            {
                s.CancellationReason,
                s.Quantity,
                Price = s.CustomPrice ?? s.Plan.Price,
                Months = (int)s.Plan.Cycle
            })
            .ToListAsync(ct);

        var reasons = rows
            .GroupBy(r => r.CancellationReason)
            .Select(g => new ChurnReasonRow(
                g.Key,
                ChurnReasonRow.TextOf(g.Key),
                g.Count(),
                Math.Round(g.Sum(x => x.Price * x.Quantity / x.Months),
                           2, MidpointRounding.AwayFromZero)))
            .OrderByDescending(r => r.Count)
            .ToList();

        return new ChurnAnalysis(
            From: from,
            To: to,
            CancelledCount: rows.Count,
            LostMrr: Math.Round(rows.Sum(r => r.Price * r.Quantity / r.Months),
                                2, MidpointRounding.AwayFromZero),
            Reasons: reasons);
    }

    // ==================================================================
    // Oluşturma
    // ==================================================================

    /// <summary>
    /// Yeni abonelik. Deneme süresi varsa Trialing başlar ve ilk fatura deneme
    /// bittiğinde kesilir; yoksa Active başlar ve ilk fatura başlangıç günü kesilir.
    /// </summary>
    public async Task<Guid> CreateAsync(SubscriptionForm form, CancellationToken ct = default)
    {
        if (form.Quantity <= 0)
            throw new DomainException("Miktar sıfırdan büyük olmalıdır.");
        if (form.CustomPrice is < 0)
            throw new DomainException("Özel fiyat negatif olamaz.");

        await using var db = factory.Create();

        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == form.PartyId, ct)
                    ?? throw new DomainException("Cari kart bulunamadı.");
        party.EnsureCanBeInvoiced();

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == form.PlanId, ct)
                   ?? throw new DomainException("Plan bulunamadı.");

        if (!plan.IsActive)
            throw new DomainException($"'{plan.Name}' planı pasif, abonelik açılamaz.");

        var anchor = form.BillingAnchorDay ?? form.StartDate.Day;
        if (anchor is < 1 or > 31)
            throw new DomainException("Faturalandırma günü 1–31 aralığında olmalıdır.");

        var trialDays = form.TrialDays ?? plan.TrialDays;

        var sub = new Subscription
        {
            PartyId = party.Id,
            PlanId = plan.Id,
            StartDate = form.StartDate,
            BillingAnchorDay = anchor,
            CustomPrice = form.CustomPrice,
            Quantity = form.Quantity,
            Notes = form.Notes
        };

        if (trialDays > 0)
        {
            // Deneme boyunca fatura kesilmez; ilk fatura deneme bitiminin ERTESİ günü.
            sub.Status = SubscriptionStatus.Trialing;
            sub.TrialEndsOn = form.StartDate.AddDays(trialDays);
            sub.NextBillingDate = sub.TrialEndsOn.Value.AddDays(1);
        }
        else
        {
            sub.Status = SubscriptionStatus.Active;
            sub.NextBillingDate = form.StartDate;
        }

        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(ct);

        return sub.Id;
    }

    /// <summary>
    /// Sihirbaz önizlemesi: "ilk fatura ne zaman, ne kadar". Kayıt yapmaz.
    /// Kullanıcının en çok sorduğu soru bu — formu doldururken görmeli.
    /// </summary>
    public async Task<SubscriptionPreview> PreviewAsync(
        SubscriptionForm form, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var plan = await db.Plans.AsNoTracking()
                       .FirstOrDefaultAsync(p => p.Id == form.PlanId, ct)
                   ?? throw new DomainException("Plan bulunamadı.");

        var anchor = form.BillingAnchorDay ?? form.StartDate.Day;
        var trialDays = form.TrialDays ?? plan.TrialDays;

        DateOnly? trialEnds = trialDays > 0 ? form.StartDate.AddDays(trialDays) : null;
        var firstBilling = trialEnds?.AddDays(1) ?? form.StartDate;
        var periodEnd = BillingSchedule.PeriodEnd(firstBilling, plan.Cycle, anchor);

        var price = form.CustomPrice ?? plan.Price;

        return new SubscriptionPreview(
            FirstBillingDate: firstBilling,
            FirstAmount: Math.Round(price * form.Quantity, 2, MidpointRounding.AwayFromZero),
            Currency: plan.Currency,
            TrialEndsOn: trialEnds,
            PeriodStart: firstBilling,
            PeriodEnd: periodEnd,
            CycleText: plan.CycleText);
    }

    // ==================================================================
    // Detay
    // ==================================================================

    /// <summary>
    /// Abonelik detayı + ürettiği faturalar. Tek sorgu turunda — panel başına
    /// ayrı çağrı yapılmaz.
    /// </summary>
    public async Task<SubscriptionDetail?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var sub = await db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .Include(s => s.Party)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (sub is null) return null;

        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.SubscriptionId == id)
            .OrderByDescending(i => i.IssueDate)
            .Select(i => new SubscriptionInvoiceRow(
                i.Id, i.Number, i.IssueDate, i.PeriodStart, i.PeriodEnd,
                i.GrandTotal, i.PaidAmount, i.Status))
            .ToListAsync(ct);

        return new SubscriptionDetail(
            Id: sub.Id,
            PartyId: sub.PartyId,
            PartyTitle: sub.Party.Title,
            PlanId: sub.PlanId,
            PlanName: sub.Plan.Name,
            PlanCode: sub.Plan.Code,
            Cycle: sub.Plan.Cycle,
            CycleText: sub.Plan.CycleText,
            Status: sub.Status,
            StatusText: sub.StatusText,
            StartDate: sub.StartDate,
            EndDate: sub.EndDate,
            TrialEndsOn: sub.TrialEndsOn,
            CancelledOn: sub.CancelledOn,
            PausedOn: sub.PausedOn,
            NextBillingDate: sub.NextBillingDate,
            BillingAnchorDay: sub.BillingAnchorDay,
            EffectivePrice: sub.CustomPrice ?? sub.Plan.Price,
            CustomPrice: sub.CustomPrice,
            PlanPrice: sub.Plan.Price,
            Currency: sub.Plan.Currency,
            Quantity: sub.Quantity,
            Notes: sub.Notes,
            Invoices: invoices);
    }

    // ==================================================================
    // Yaşam döngüsü
    // ==================================================================

    public async Task PauseAsync(Guid id, DateOnly on, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, ct)
                  ?? throw new DomainException("Abonelik bulunamadı.");

        sub.Pause(on);
        await db.SaveChangesAsync(ct);
    }

    public async Task ResumeAsync(Guid id, DateOnly on, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var sub = await db.Subscriptions
                      .Include(s => s.Plan)          // Resume takvimi ileri sarmak için Cycle okur
                      .FirstOrDefaultAsync(s => s.Id == id, ct)
                  ?? throw new DomainException("Abonelik bulunamadı.");

        sub.Resume(on);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================
    // Plan değişikliği
    // ==================================================================

    /// <summary>
    /// Değişikliği UYGULAMADAN farkı hesaplar. Kullanıcı onaylamadan önce
    /// "ne kadar ödeyeceğim" sorusunun cevabı.
    /// </summary>
    public async Task<PlanChangePreview> PreviewPlanChangeAsync(
        Guid subscriptionId, Guid newPlanId, DateOnly on, decimal? newCustomPrice = null,
        CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var sub = await db.Subscriptions.AsNoTracking()
                      .Include(s => s.Plan)
                      .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
                  ?? throw new DomainException("Abonelik bulunamadı.");

        var newPlan = await db.Plans.AsNoTracking()
                          .FirstOrDefaultAsync(p => p.Id == newPlanId, ct)
                      ?? throw new DomainException("Plan bulunamadı.");

        // İçinde bulunulan dönem: bir sonraki fatura tarihinden geriye doğru bir döngü
        var periodStart = PreviousPeriodStart(sub.NextBillingDate, sub.Plan.Cycle,
                                              sub.BillingAnchorDay);
        var periodEnd = sub.NextBillingDate.AddDays(-1);

        var oldPeriodPrice = (sub.CustomPrice ?? sub.Plan.Price) * sub.Quantity;
        var newPeriodPrice = (newCustomPrice ?? newPlan.Price) * sub.Quantity;

        var proration = Subscription.ProrationAmount(
            oldPeriodPrice, newPeriodPrice, periodStart, periodEnd, on);

        return new PlanChangePreview(
            CurrentPlanName: sub.Plan.Name,
            NewPlanName: newPlan.Name,
            CurrentPeriodPrice: Math.Round(oldPeriodPrice, 2, MidpointRounding.AwayFromZero),
            NewPeriodPrice: Math.Round(newPeriodPrice, 2, MidpointRounding.AwayFromZero),
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            ChangeDate: on,
            ProrationAmount: proration,
            NextBillingDate: BillingSchedule.NextPeriodStart(on, newPlan.Cycle,
                                                            sub.BillingAnchorDay),
            Currency: newPlan.Currency);
    }

    /// <summary>
    /// Planı değiştirir. Dönem ortası farkı çağıran tarafta faturalanır —
    /// bu metot yalnızca aboneliği günceller (fark faturası ayrı bir karar).
    /// </summary>
    public async Task ChangePlanAsync(
        Guid subscriptionId, Guid newPlanId, DateOnly on, decimal? newCustomPrice = null,
        CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var sub = await db.Subscriptions
                      .Include(s => s.Plan)
                      .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
                  ?? throw new DomainException("Abonelik bulunamadı.");

        var newPlan = await db.Plans.FirstOrDefaultAsync(p => p.Id == newPlanId, ct)
                      ?? throw new DomainException("Plan bulunamadı.");

        sub.ChangePlan(newPlan, on, newCustomPrice);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Bir sonraki dönem başından geriye giderek içinde bulunulan dönemin başını bulur.
    /// Çapa günü mantığı korunur — AddMonths(-n) tek başına günü kaydırır.
    /// </summary>
    private static DateOnly PreviousPeriodStart(DateOnly nextStart, BillingCycle cycle, int anchor)
    {
        var back = nextStart.AddMonths(-(int)cycle);
        var daysInMonth = DateTime.DaysInMonth(back.Year, back.Month);
        return new DateOnly(back.Year, back.Month, Math.Min(anchor, daysInMonth));
    }
}
