using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Parties;
using NexusErp.Application.Events;
using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

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
                p.Price / (int)p.Cycle))
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
                                  CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, ct)
                  ?? throw new DomainException("Abonelik bulunamadı.");

        sub.Cancel(on, immediately);

        db.AddEvent(new SubscriptionCancelled(sub.Id, sub.PartyId, on, immediately),
                    DateTimeOffset.UtcNow);

        await db.SaveChangesAsync(ct);
    }
}
