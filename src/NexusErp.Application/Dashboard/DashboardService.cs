using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Subscriptions;
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

/// <summary>Yaşlandırma özeti — beş kova, rapor sayfasının toplam satırıyla aynı mantık.</summary>
public sealed record AgingBuckets(
    decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public decimal Total => NotDue + Days1To30 + Days31To60 + Days61To90 + Over90;

    public static readonly AgingBuckets Empty = new(0m, 0m, 0m, 0m, 0m);
}

/// <summary>Bu ayki cironun ürün bazında kırılımı (ilk 4 + "Diğer").</summary>
public sealed record ProductShare(string Name, decimal Amount);

/// <summary>
/// Abonelik hareketi. MrrDelta bu ay başlayan ve iptal edilen aboneliklerin
/// aylık tutar farkıdır — geçmiş MRR saklanmadığı için yaklaşık değerdir.
/// </summary>
public sealed record SubscriptionMovement(
    int NewCount, int CancelledCount, decimal MrrDelta, decimal ChurnRate,
    int BillingThisWeek, decimal BillingThisWeekAmount)
{
    public static readonly SubscriptionMovement Empty = new(0, 0, 0m, 0m, 0, 0m);
}

/// <summary>Faturaya eşleşmemiş (avans) tahsilat.</summary>
public sealed record UnallocatedPayment(
    Guid PaymentId, string? Number, string PartyTitle, string? Reference, decimal Amount);

public sealed record DashboardSummary(
    decimal OpenReceivables,
    decimal OverdueReceivables,
    int OverdueInvoiceCount,
    decimal OpenPayables,
    decimal OverduePayables,
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
    IReadOnlyList<StatusBreakdown> StatusBreakdown,
    AgingBuckets AgingBuckets,
    IReadOnlyList<ProductShare> ProductBreakdown,
    SubscriptionMovement SubscriptionMovement,
    decimal DaysSalesOutstanding,
    int IssuedThisMonth,
    IReadOnlyList<UnallocatedPayment> UnallocatedPayments,
    ChurnAnalysis Churn)
{
    /// <summary>Alacak eksi borç. Nakit değil, TAHAKKUK pozisyonu.</summary>
    public decimal NetPosition => OpenReceivables - OpenPayables;

    public decimal RevenueChangePercent => PreviousMonthRevenue == 0
        ? 0m
        : Math.Round((MonthRevenue - PreviousMonthRevenue) / PreviousMonthRevenue * 100m, 1);
}

/// <summary>
/// Dashboard'ın TAMAMI tek serviste. Her kart için ayrı servis çağırmak
/// 8 veri tabanı turu ve Blazor'da 8 ayrı render döngüsü demektir.
/// </summary>
public sealed class DashboardService(IAppDbContextFactory factory)
{
    public async Task<DashboardSummary> GetAsync(DateOnly today, CancellationToken ct = default)
    {
        // Tek context: dashboard'ın 8 sorgusu aynı anlık görüntüden okusun.
        await using var db = factory.Create();

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var trendStart = monthStart.AddMonths(-11);

        // ⚠️ ALIŞ faturası bir ALACAK değil BORÇtur. Buraya karışırsa "açık alacak"
        // kartı olduğundan büyük görünür ve en çok alacaklı cari listesine kendi
        // tedarikçilerimiz düşer. Proforma da bağlayıcı olmadığı için dışarıda.
        var openInvoices = db.Invoices.Where(i =>
            (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid)
            && i.Type != InvoiceType.Proforma
            && i.Type != InvoiceType.Purchase);

        // Borçlar: aynı ölçüt, alış tarafı.
        var openPurchases = db.Invoices.Where(i =>
            (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid)
            && i.Type == InvoiceType.Purchase);

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

        var payables = await openPurchases
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Open = g.Sum(i => i.GrandTotal - i.PaidAmount),
                Overdue = g.Sum(i => i.DueDate < today ? i.GrandTotal - i.PaidAmount : 0m)
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
        // ⚠️ Saf KULLANIM planları MRR'a girmez: taahhüt edilmiş yinelenen gelir yok,
        // tutar her ay kullanımla değişir. MRR "tahmin edilebilir gelir" demek;
        // değişken kullanımı buraya karıştırmak metriği anlamsızlaştırır.
        var subs = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active
                     && s.Plan.BillingModel != BillingModel.Metered)
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
            .Where(i => i.Type != InvoiceType.Proforma && i.Type != InvoiceType.Purchase)
            .GroupBy(i => i.Status)
            .Select(g => new StatusBreakdown(g.Key, g.Count(), g.Sum(i => i.GrandTotal)))
            .ToListAsync(ct);

        // --- Sayaçlar ---
        var counters = await db.Invoices
            .Where(i => i.Type != InvoiceType.Purchase)
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

        // ---------------------------------------------------------------
        // Aşağıdaki bloklar arayüzün yeni panelleri için. Hepsi BU çağrının
        // içinde üretilir — panel başına ayrı servis çağrısı yapılmaz.
        // ---------------------------------------------------------------

        // --- Yaşlandırma özeti (rapor sayfasıyla aynı kova mantığı) ---
        var d30 = today.AddDays(-30);
        var d60 = today.AddDays(-60);
        var d90 = today.AddDays(-90);

        var aging = await openInvoices
            .GroupBy(_ => 1)
            .Select(g => new AgingBuckets(
                g.Sum(i => i.DueDate >= today ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < today && i.DueDate >= d30 ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < d30 && i.DueDate >= d60 ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < d60 && i.DueDate >= d90 ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < d90 ? i.GrandTotal - i.PaidAmount : 0m)))
            .FirstOrDefaultAsync(ct);

        // --- Bu ayki cironun ürün kırılımı ---
        var productRows = await db.InvoiceLines
            .Where(l => l.Invoice.Status != InvoiceStatus.Draft
                     && l.Invoice.Status != InvoiceStatus.Cancelled
                     && l.Invoice.Type == InvoiceType.Sales
                     && l.Invoice.IssueDate >= monthStart)
            .GroupBy(l => l.ProductName)
            // ⚠️ Sıralama GRUPLAMA aşamasında. Select ile ProductShare'e projekte
            // ettikten sonra .OrderByDescending(p => p.Amount) yazarsan EF Core
            // çeviremez: projeksiyon sonrası kayıt üyesine SQL'de erişemiyor.
            .OrderByDescending(g => g.Sum(l => l.TaxBase))
            .Select(g => new ProductShare(g.Key, g.Sum(l => l.TaxBase)))
            .ToListAsync(ct);

        // İlk 4 + "Diğer" — panel dar, uzun liste okunmuyor
        var productBreakdown = productRows.Count <= 5
            ? productRows
            : [.. productRows.Take(4), new ProductShare("Diğer", productRows.Skip(4).Sum(p => p.Amount))];

        // --- Abonelik hareketi ---
        var weekEnd = today.AddDays(7);

        var subMovementRows = await db.Subscriptions
            .Where(s => s.StartDate >= monthStart
                     || (s.CancelledOn != null && s.CancelledOn >= monthStart)
                     || (s.Status == SubscriptionStatus.Active
                         && s.NextBillingDate >= today && s.NextBillingDate <= weekEnd))
            .Select(s => new
            {
                s.Status,
                s.StartDate,
                s.CancelledOn,
                s.NextBillingDate,
                s.Quantity,
                // ⚠️ Saf kullanım planında Plan.Price sabit ücret DEĞİL, sıfır kabul
                // edilmeli — yoksa MRR deltası olmayan bir gelir gösterir.
                Price = s.Plan.BillingModel == BillingModel.Metered
                    ? 0m : s.CustomPrice ?? s.Plan.Price,
                Months = (int)s.Plan.Cycle
            })
            .ToListAsync(ct);

        var newSubs = subMovementRows.Where(s => s.StartDate >= monthStart).ToList();
        var cancelledSubs = subMovementRows.Where(s => s.CancelledOn >= monthStart).ToList();
        var weekSubs = subMovementRows
            .Where(s => s.Status == SubscriptionStatus.Active
                     && s.NextBillingDate >= today && s.NextBillingDate <= weekEnd)
            .ToList();

        static decimal MonthlyOf(decimal price, decimal qty, int months) => price * qty / months;

        var movement = new SubscriptionMovement(
            NewCount: newSubs.Count,
            CancelledCount: cancelledSubs.Count,
            // ⚠️ Yaklaşık: geçmiş MRR saklanmıyor, bu ayki giriş/çıkış farkı alınıyor.
            MrrDelta: Math.Round(
                newSubs.Sum(s => MonthlyOf(s.Price, s.Quantity, s.Months))
                - cancelledSubs.Sum(s => MonthlyOf(s.Price, s.Quantity, s.Months)),
                2, MidpointRounding.AwayFromZero),
            ChurnRate: subs.Count + cancelledSubs.Count == 0
                ? 0m
                : Math.Round(cancelledSubs.Count * 100m / (subs.Count + cancelledSubs.Count),
                             1, MidpointRounding.AwayFromZero),
            BillingThisWeek: weekSubs.Count,
            BillingThisWeekAmount: Math.Round(
                weekSubs.Sum(s => s.Price * s.Quantity), 2, MidpointRounding.AwayFromZero));

        // --- Ortalama tahsil süresi (DSO) ---
        // ⚠️ DateOnly farkı SQL'e çevrilemediği için son 12 ayın eşleştirmeleri
        // belleğe alınıp orada hesaplanıyor. Pencere sınırlı olduğu için maliyeti kabul edilebilir.
        var allocationRows = await db.PaymentAllocations
            .Where(a => a.AllocatedOn >= trendStart)
            .Select(a => new { a.Amount, a.AllocatedOn, a.Invoice.IssueDate })
            .ToListAsync(ct);

        var weightSum = allocationRows.Sum(a => a.Amount);
        var dso = weightSum == 0m
            ? 0m
            : Math.Round(
                allocationRows.Sum(a => a.Amount * (a.AllocatedOn.DayNumber - a.IssueDate.DayNumber))
                / weightSum,
                1, MidpointRounding.AwayFromZero);

        // --- Bu ay kesilen fatura adedi ---
        var issuedThisMonth = await db.Invoices
            .CountAsync(i => i.Status != InvoiceStatus.Draft
                          && i.Status != InvoiceStatus.Cancelled
                          && i.IssueDate >= monthStart, ct);

        // --- Churn: neden kaybettik ---
        // Son 90 gün; tek ay örneklem olarak çok küçük kalıyor, sebep dağılımı
        // anlamsız görünüyor.
        var churnFrom = today.AddDays(-90);

        var churnRows = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Cancelled
                     && s.CancelledOn != null
                     && s.CancelledOn >= churnFrom && s.CancelledOn <= today)
            .Select(s => new
            {
                s.CancellationReason,
                s.Quantity,
                Price = s.Plan.BillingModel == BillingModel.Metered
                    ? 0m : s.CustomPrice ?? s.Plan.Price,
                Months = (int)s.Plan.Cycle
            })
            .ToListAsync(ct);

        var churn = new ChurnAnalysis(
            From: churnFrom,
            To: today,
            CancelledCount: churnRows.Count,
            LostMrr: Math.Round(churnRows.Sum(r => r.Price * r.Quantity / r.Months),
                                2, MidpointRounding.AwayFromZero),
            Reasons: [.. churnRows
                .GroupBy(r => r.CancellationReason)
                .Select(g => new ChurnReasonRow(
                    g.Key,
                    ChurnReasonRow.TextOf(g.Key),
                    g.Count(),
                    Math.Round(g.Sum(x => x.Price * x.Quantity / x.Months),
                               2, MidpointRounding.AwayFromZero)))
                .OrderByDescending(r => r.Count)]);

        // --- Eşleşmemiş (avans) tahsilatlar ---
        var unallocated = await db.Payments
            .Where(p => !p.IsCancelled && p.AllocatedAmount < p.Amount)
            .OrderByDescending(p => p.PaymentDate)
            .Take(5)
            .Select(p => new UnallocatedPayment(
                p.Id, p.Number, p.Party.Title, p.Reference, p.Amount - p.AllocatedAmount))
            .ToListAsync(ct);

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
            OpenPayables: payables?.Open ?? 0m,
            OverduePayables: payables?.Overdue ?? 0m,
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
            StatusBreakdown: breakdown,
            AgingBuckets: aging ?? AgingBuckets.Empty,
            ProductBreakdown: productBreakdown,
            SubscriptionMovement: movement,
            DaysSalesOutstanding: dso,
            IssuedThisMonth: issuedThisMonth,
            UnallocatedPayments: unallocated,
            Churn: churn);
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
