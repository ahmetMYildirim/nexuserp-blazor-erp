using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Events;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Subscriptions;

/// <summary>Bir dunning turunun sonucu.</summary>
public sealed record DunningRunResult(
    int MarkedPastDue, int RemindersSent, int Suspended, int Recovered)
{
    public string Summary => (MarkedPastDue, RemindersSent, Suspended, Recovered) switch
    {
        (0, 0, 0, 0) => "Ödenmemiş abonelik faturası yok.",
        _ => $"{MarkedPastDue} abonelik gecikmeye düştü, {RemindersSent} hatırlatma, " +
             $"{Suspended} askıya alma, {Recovered} tahsilat sonrası düzelme."
    };
}

/// <summary>
/// Ödenmeyen abonelik faturalarını takip eder (dunning).
///
/// Akış: vadesi geçmiş ödenmemiş fatura → abonelik PastDue → 3/7/14. günlerde
/// hatırlatma → 21. günde askıya alma. Borç kapanırsa abonelik normale döner.
///
/// ⚠️ Her adım OLAY yayınlar, e-posta göndermez. Bildirim tüketicinin işi;
/// bu servis yalnızca durumu yönetir. Böylece aynı akış SMS, push ya da
/// muhasebeye bildirim gibi başka tüketicilerle de beslenebilir.
/// </summary>
public sealed class DunningService(
    IAppDbContextFactory factory,
    ILogger<DunningService> logger)
{
    public async Task<DunningRunResult> RunAsync(DateOnly asOf, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        // Abonelikten üretilmiş, vadesi geçmiş ve tamamı tahsil edilmemiş faturalar.
        // Cari başına değil ABONELİK başına bakıyoruz: aynı carinin başka bir
        // aboneliği ödenmiş olabilir, onu cezalandırmamalıyız.
        var overdue = await db.Invoices
            .Where(i => i.SubscriptionId != null
                     && i.DueDate < asOf
                     && (i.Status == InvoiceStatus.Issued
                      || i.Status == InvoiceStatus.PartiallyPaid))
            .GroupBy(i => i.SubscriptionId!.Value)
            .Select(g => new
            {
                SubscriptionId = g.Key,
                Amount = g.Sum(i => i.GrandTotal - i.PaidAmount),
                OldestDue = g.Min(i => i.DueDate)
            })
            .ToListAsync(ct);

        var overdueIds = overdue.Select(o => o.SubscriptionId).ToList();

        // Takipteki abonelikler + borcu kapanmış olabilecekler tek sorguda
        var subs = await db.Subscriptions
            .Include(s => s.Party)
            .Include(s => s.Plan)
            .Where(s => overdueIds.Contains(s.Id) || s.Status == SubscriptionStatus.PastDue)
            .ToListAsync(ct);

        var marked = 0;
        var reminders = 0;
        var suspended = 0;
        var recovered = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var sub in subs)
        {
            var debt = overdue.FirstOrDefault(o => o.SubscriptionId == sub.Id);

            // --- Borç kapandı mı? ---
            if (debt is null)
            {
                if (sub.Status == SubscriptionStatus.PastDue)
                {
                    sub.ClearPastDue();
                    db.AddEvent(new SubscriptionRecovered(
                        sub.Id, sub.PartyId, sub.Party.Title, asOf), now);
                    recovered++;
                }
                continue;
            }

            // İptal/duraklatılmış aboneliği takibe alma
            if (sub.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Paused)
                continue;

            // --- Gecikmeye düşür ---
            var wasPastDue = sub.Status == SubscriptionStatus.PastDue;

            // ⚠️ Başlangıç, en eski ödenmemiş faturanın VADESİ — turun çalıştığı
            // gün değil. Aksi halde işçi ilk kez geç çalışırsa gecikme günü sıfırdan
            // sayılır ve müşteri hak etmediği bir süre daha bekler.
            sub.MarkPastDue(debt.OldestDue);

            if (!wasPastDue)
            {
                db.AddEvent(new SubscriptionPastDue(
                    sub.Id, sub.PartyId, sub.Party.Title,
                    debt.Amount, sub.Plan.Currency, debt.OldestDue), now);
                marked++;
            }

            // --- Askıya alma (hatırlatmalardan önce kontrol edilir) ---
            if (sub.ShouldSuspend(asOf))
            {
                var days = sub.DaysPastDue(asOf);
                sub.Suspend(asOf);

                db.AddEvent(new SubscriptionSuspended(
                    sub.Id, sub.PartyId, sub.Party.Title,
                    days, debt.Amount, sub.Plan.Currency, asOf), now);

                suspended++;
                continue;
            }

            // --- Hatırlatma ---
            if (sub.NextDunningLevel(asOf) is { } level)
            {
                db.AddEvent(new SubscriptionPaymentReminder(
                    sub.Id, sub.PartyId, sub.Party.Title,
                    level, sub.DaysPastDue(asOf), debt.Amount, sub.Plan.Currency), now);

                sub.DunningLevel = level;
                reminders++;
            }
        }

        await db.SaveChangesAsync(ct);

        var result = new DunningRunResult(marked, reminders, suspended, recovered);

        if (marked + reminders + suspended + recovered > 0)
            logger.LogInformation("Dunning turu ({AsOf}): {Summary}", asOf, result.Summary);

        return result;
    }
}
