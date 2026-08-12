using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Payments;

public sealed class PartyBalanceService(IAppDbContextFactory factory)
{
    /// <summary>Bakiye = SUM(Borç) − SUM(Alacak). Pozitif = müşteri bize borçlu.</summary>
    public async Task<decimal> GetBalanceAsync(Guid partyId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await db.PartyLedgerEntries
            .Where(e => e.PartyId == partyId)
            .SumAsync(e => e.Debit - e.Credit, ct);
    }

    /// <summary>Cari ekstre — devir satırı + hareketler + yürüyen bakiye.</summary>
    public async Task<IReadOnlyList<StatementRow>> GetStatementAsync(
        Guid partyId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        // Devir: dönem başından ÖNCEKİ tüm hareketlerin bakiyesi
        var opening = await db.PartyLedgerEntries
            .Where(e => e.PartyId == partyId && e.EntryDate < from)
            .SumAsync(e => e.Debit - e.Credit, ct);

        var entries = await db.PartyLedgerEntries
            .Where(e => e.PartyId == partyId && e.EntryDate >= from && e.EntryDate <= to)
            .OrderBy(e => e.EntryDate).ThenBy(e => e.CreatedAt)
            .Select(e => new { e.EntryDate, e.Description, e.DocumentNumber, e.Debit, e.Credit })
            .ToListAsync(ct);

        // Yürüyen bakiye BELLEKTE hesaplanıyor. SUM(...) OVER (ORDER BY ...) window
        // fonksiyonu ile SQL'de de yapılabilirdi ve 100.000 satırda gerekir; ekstrede
        // tarih aralığı sınırlı olduğu için okunabilirliği seçtik (bilinçli ödün).
        var rows = new List<StatementRow>(entries.Count + 1)
        {
            new(from.AddDays(-1), "Devir", null, 0m, 0m, opening)
        };

        var running = opening;
        foreach (var e in entries)
        {
            running += e.Debit - e.Credit;
            rows.Add(new StatementRow(e.EntryDate, e.Description, e.DocumentNumber,
                                      e.Debit, e.Credit, running));
        }

        return rows;
    }

    /// <summary>
    /// Yaşlandırma raporu — işletmenin nakit sağlığını gösteren en kritik rapor.
    /// TEK LINQ sorgusu; koşullu SUM'lar SQL'de CASE WHEN olarak çalışır.
    /// Veriyi belleğe çekip GroupBy yapmak 100.000 faturada uygulamayı öldürür.
    /// </summary>
    public async Task<IReadOnlyList<AgingRow>> GetAgingAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var d30 = asOf.AddDays(-30);
        var d60 = asOf.AddDays(-60);
        var d90 = asOf.AddDays(-90);

        // ⚠️ Filtre ve sıralama GRUPLAMA aşamasında yapılmalı.
        // Select ile AgingRow'a projekte ettikten sonra .Where(r => r.Total > 0) yazarsan
        // EF Core ifadeyi çeviremez ("The LINQ expression could not be translated"):
        // projeksiyon sonrası kayıt üyesine SQL'de erişemiyor.
        return await db.Invoices
            .Where(i => (i.Status == InvoiceStatus.Issued
                      || i.Status == InvoiceStatus.PartiallyPaid)
                     && i.Type != InvoiceType.Proforma)
            .GroupBy(i => new { i.PartyId, i.PartyTitle })
            .Where(g => g.Sum(i => i.GrandTotal - i.PaidAmount) > 0)          // HAVING
            .OrderByDescending(g => g.Sum(i => i.GrandTotal - i.PaidAmount))  // ORDER BY
            .Select(g => new AgingRow(
                g.Key.PartyId,
                g.Key.PartyTitle,
                g.Sum(i => i.DueDate >= asOf ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < asOf && i.DueDate >= d30 ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < d30 && i.DueDate >= d60 ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < d60 && i.DueDate >= d90 ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.DueDate < d90 ? i.GrandTotal - i.PaidAmount : 0m),
                g.Sum(i => i.GrandTotal - i.PaidAmount)))
            .ToListAsync(ct);
    }
}
