using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Dashboard;

public sealed record MonthlyRevenue(int Year, int Month, decimal Amount)
{
    public string Label => new DateOnly(Year, Month, 1).ToString("MMM yy");
}

public sealed record TopDebtor(Guid PartyId, string Title, decimal Amount, int InvoiceCount);

public sealed record UpcomingInvoice(
    Guid Id, string Number, string PartyTitle, DateOnly DueDate, decimal Remaining, int DaysLeft);

public sealed record StatusBreakdown(InvoiceStatus Status, int Count, decimal Amount);

public sealed record DashboardSummary(
    decimal OpenReceivables,
    decimal OverdueReceivables,
    int OverdueInvoiceCount,
    decimal Mrr,
    decimal Arr,
    int ActiveSubscriptions,
    decimal MonthRevenue,
    decimal PreviousMonthRevenue,
    decimal MonthCollected,
    int DraftInvoiceCount,
    int InvoiceCount,
    int PartyCount,
    decimal AverageInvoice,
    decimal CollectionRate,
    IReadOnlyList<MonthlyRevenue> RevenueTrend,
    IReadOnlyList<MonthlyRevenue> CollectionTrend,
    IReadOnlyList<TopDebtor> TopDebtors,
    IReadOnlyList<UpcomingInvoice> UpcomingDue,
    IReadOnlyList<StatusBreakdown> StatusBreakdown)
{
    public decimal RevenueChangePercent => PreviousMonthRevenue == 0
        ? 0m
        : Math.Round((MonthRevenue - PreviousMonthRevenue) / PreviousMonthRevenue * 100m, 1);
}

/// <summary>
/// Dashboard'ın TAMAMI tek serviste. Her kart için ayrı servis çağırmak
/// 8 veri tabanı turu ve Blazor'da 8 ayrı render döngüsü demektir.
/// </summary>
public sealed class DashboardService(IAppDbContext db)
{
    public async Task<DashboardSummary> GetAsync(DateOnly today, CancellationToken ct = default)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var trendStart = monthStart.AddMonths(-11);

        var openInvoices = db.Invoices.Where(i =>
            (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid)
            && i.Type != InvoiceType.Proforma);

        // --- Alacaklar: üç ölçüt, TEK sorgu (GroupBy(_ => 1) numarası) ---
        var receivables = await openInvoices
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Open = g.Sum(i => i.GrandTotal - i.PaidAmount),
                Overdue = g.Sum(i => i.DueDate < today ? i.GrandTotal - i.PaidAmount : 0m),
                OverdueCount = g.Count(i => i.DueDate < today)
            })
            .FirstOrDefaultAsync(ct);

        // --- Ciro trendi: son 12 ay, tek gruplama ---
        // ⚠️ TaxBaseTotal (KDV hariç) — KDV işletmenin geliri değil, devlet adına tahsilat.
        var trend = await db.Invoices
            .Where(i => i.Status != InvoiceStatus.Draft
                     && i.Status != InvoiceStatus.Cancelled
                     && i.Type == InvoiceType.Sales
                     && i.IssueDate >= trendStart)
            .GroupBy(i => new { i.IssueDate.Year, i.IssueDate.Month })
            .Select(g => new MonthlyRevenue(g.Key.Year, g.Key.Month, g.Sum(i => i.TaxBaseTotal)))
            .ToListAsync(ct);

        // --- Tahsilat trendi ---
        var collections = await db.Payments
            .Where(p => !p.IsCancelled && p.PaymentDate >= trendStart)
            .GroupBy(p => new { p.PaymentDate.Year, p.PaymentDate.Month })
            .Select(g => new MonthlyRevenue(g.Key.Year, g.Key.Month, g.Sum(p => p.Amount)))
            .ToListAsync(ct);

        // --- Abonelikler (MRR) ---
        var subs = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active)
            .Select(s => new
            {
                s.Quantity,
                Price = s.CustomPrice ?? s.Plan.Price,
                Months = (int)s.Plan.Cycle
            })
            .ToListAsync(ct);

        var mrr = Math.Round(subs.Sum(s => s.Price * s.Quantity / s.Months),
                             2, MidpointRounding.AwayFromZero);

        // --- En çok alacaklı 5 cari ---
        // ⚠️ Sıralama GRUPLAMA aşamasında. Select ile TopDebtor'a projekte ettikten sonra
        // .OrderByDescending(x => x.Amount) yazarsan EF Core çeviremez —
        // projeksiyon sonrası kayıt üyesine SQL'de erişemiyor.
        var topDebtors = await openInvoices
            .GroupBy(i => new { i.PartyId, i.PartyTitle })
            .OrderByDescending(g => g.Sum(i => i.GrandTotal - i.PaidAmount))
            .Take(5)
            .Select(g => new TopDebtor(
                g.Key.PartyId, g.Key.PartyTitle,
                g.Sum(i => i.GrandTotal - i.PaidAmount), g.Count()))
            .ToListAsync(ct);

        // --- Yaklaşan vadeler ---
        var upcoming = await openInvoices
            .Where(i => i.DueDate >= today)
            .OrderBy(i => i.DueDate)
            .Take(5)
            .Select(i => new UpcomingInvoice(
                i.Id, i.Number!, i.PartyTitle, i.DueDate,
                i.GrandTotal - i.PaidAmount,
                i.DueDate.DayNumber - today.DayNumber))
            .ToListAsync(ct);

        // --- Durum dağılımı ---
        var breakdown = await db.Invoices
            .Where(i => i.Type != InvoiceType.Proforma)
            .GroupBy(i => i.Status)
            .Select(g => new StatusBreakdown(g.Key, g.Count(), g.Sum(i => i.GrandTotal)))
            .ToListAsync(ct);

        // --- Sayaçlar ---
        var counters = await db.Invoices
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Drafts = g.Count(i => i.Status == InvoiceStatus.Draft),
                Issued = g.Count(i => i.Status != InvoiceStatus.Draft
                                   && i.Status != InvoiceStatus.Cancelled),
                GrandSum = g.Sum(i => i.Status != InvoiceStatus.Draft
                                   && i.Status != InvoiceStatus.Cancelled
                                        ? i.GrandTotal : 0m),
                PaidSum = g.Sum(i => i.PaidAmount)
            })
            .FirstOrDefaultAsync(ct);

        var partyCount = await db.Parties.CountAsync(p => p.IsActive, ct);

        var monthCollected = await db.Payments
            .Where(p => !p.IsCancelled && p.PaymentDate >= monthStart)
            .SumAsync(p => p.Amount, ct);

        // Zaman serisinde EKSİK AYLARI DOLDUR — veri tabanı sadece dolu ayları döner,
        // ham veriyi grafiğe verirsen çizgi yalan söyler.
        var fullTrend = FillMonths(trend, trendStart);
        var fullCollections = FillMonths(collections, trendStart);

        var issuedCount = counters?.Issued ?? 0;
        var grandSum = counters?.GrandSum ?? 0m;

        return new DashboardSummary(
            OpenReceivables: receivables?.Open ?? 0m,
            OverdueReceivables: receivables?.Overdue ?? 0m,
            OverdueInvoiceCount: receivables?.OverdueCount ?? 0,
            Mrr: mrr,
            Arr: Math.Round(mrr * 12m, 2, MidpointRounding.AwayFromZero),
            ActiveSubscriptions: subs.Count,
            MonthRevenue: fullTrend.LastOrDefault()?.Amount ?? 0m,
            PreviousMonthRevenue: fullTrend.Count >= 2 ? fullTrend[^2].Amount : 0m,
            MonthCollected: monthCollected,
            DraftInvoiceCount: counters?.Drafts ?? 0,
            InvoiceCount: counters?.Total ?? 0,
            PartyCount: partyCount,
            AverageInvoice: issuedCount == 0
                ? 0m
                : Math.Round(grandSum / issuedCount, 2, MidpointRounding.AwayFromZero),
            CollectionRate: grandSum == 0
                ? 0m
                : Math.Round((counters?.PaidSum ?? 0m) / grandSum * 100m, 1),
            RevenueTrend: fullTrend,
            CollectionTrend: fullCollections,
            TopDebtors: topDebtors,
            UpcomingDue: upcoming,
            StatusBreakdown: breakdown);
    }

    private static List<MonthlyRevenue> FillMonths(List<MonthlyRevenue> source, DateOnly start) =>
        Enumerable.Range(0, 12)
            .Select(offset =>
            {
                var m = start.AddMonths(offset);
                return source.FirstOrDefault(t => t.Year == m.Year && t.Month == m.Month)
                       ?? new MonthlyRevenue(m.Year, m.Month, 0m);
            })
            .ToList();
}
