using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Accounting;

/// <summary>
/// Mizan, bilanço ve gelir tablosu.
///
/// ⚠️ Üç raporun da tek ortak kuralı var: YALNIZCA kesinleşmiş (IsPosted) fişler
/// sayılır. Taslak fiş dengesiz olabilir; rapora karışsaydı mizan tutmazdı ve
/// kullanıcı olmayan bir hata arardı.
/// </summary>
public sealed class AccountingReportService(IAppDbContextFactory factory)
{
    /// <summary>
    /// Mizan: dönem içindeki her hesabın borç/alacak toplamı.
    ///
    /// Toplama veri tabanında yapılıyor (GroupBy → SQL GROUP BY). Satırları
    /// belleğe çekip C# tarafında toplamak binlerce fişte kabul edilemez hale
    /// gelirdi; ölçüm gerektiğinde ilk bakılacak yer burasıdır.
    /// </summary>
    public async Task<TrialBalance> GetTrialBalanceAsync(
        DateOnly from, DateOnly to, bool includeZeroBalance = false,
        CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var rows = await db.JournalLines.AsNoTracking()
            .Where(l => l.JournalEntry.IsPosted
                     && l.JournalEntry.EntryDate >= from
                     && l.JournalEntry.EntryDate <= to)
            .GroupBy(l => new { l.AccountCode, l.AccountName, l.Account.Type })
            .Select(g => new TrialBalanceRow(
                g.Key.AccountCode,
                g.Key.AccountName,
                g.Key.Type,
                g.Sum(l => l.Debit),
                g.Sum(l => l.Credit)))
            .ToListAsync(ct);

        if (!includeZeroBalance)
            rows = rows.Where(r => r.Debit != 0m || r.Credit != 0m).ToList();

        return new TrialBalance(from, to, rows.OrderBy(r => r.Code).ToList());
    }

    /// <summary>
    /// Bilanço: dönem başından <paramref name="asOf"/> tarihine kadar birikmiş
    /// bakiyeler. Gelir/gider hesapları bilançoya girmez; onların net sonucu
    /// "dönem net kârı" olarak pasifte tek satır gösterilir.
    /// </summary>
    public async Task<BalanceSheet> GetBalanceSheetAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        var rows = await AggregateAsync(DateOnly.MinValue, asOf, ct);

        // Varlık ve gider borçla artar → bakiye = borç − alacak.
        // Kaynak, özkaynak ve gelir alacakla artar → bakiye = alacak − borç.
        // İşareti çevirmezsek pasif kalemler bilançoda negatif görünür.
        var assets = Group("Aktif (Varlıklar)", rows, AccountType.Asset, debitNormal: true);
        var liabilities = Group("Kısa ve Uzun Vadeli Yabancı Kaynaklar", rows,
                                AccountType.Liability, debitNormal: false);
        var equity = Group("Özkaynaklar", rows, AccountType.Equity, debitNormal: false);

        var revenue = rows.Where(r => r.Type == AccountType.Revenue)
                          .Sum(r => r.Credit - r.Debit);
        var expense = rows.Where(r => r.Type == AccountType.Expense)
                          .Sum(r => r.Debit - r.Credit);

        return new BalanceSheet(asOf, assets, liabilities, equity, revenue - expense);
    }

    /// <summary>Gelir tablosu: seçilen aralıkta gelir − gider.</summary>
    public async Task<IncomeStatement> GetIncomeStatementAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await AggregateAsync(from, to, ct);

        var revenues = Group("Gelirler", rows, AccountType.Revenue, debitNormal: false);
        var expenses = Group("Giderler", rows, AccountType.Expense, debitNormal: true);

        return new IncomeStatement(from, to, revenues, expenses);
    }

    /// <summary>
    /// Yılın 12 ayı için gelir / gider / sonuç. Boş aylar sıfırla doldurulur —
    /// veri tabanı yalnızca hareket olan ayları döner ve ham veri grafiğe
    /// verilirse eksik aylar yokmuş gibi sıkışır, trend yalan söyler.
    /// </summary>
    public async Task<IReadOnlyList<MonthlyResult>> GetMonthlyResultsAsync(
        int year, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);

        var rows = await db.JournalLines.AsNoTracking()
            .Where(l => l.JournalEntry.IsPosted
                     && l.JournalEntry.EntryDate >= from
                     && l.JournalEntry.EntryDate <= to
                     && (l.Account.Type == AccountType.Revenue
                      || l.Account.Type == AccountType.Expense))
            .GroupBy(l => new
            {
                l.JournalEntry.EntryDate.Month,
                l.Account.Type
            })
            .Select(g => new
            {
                g.Key.Month,
                g.Key.Type,
                Debit = g.Sum(l => l.Debit),
                Credit = g.Sum(l => l.Credit)
            })
            .ToListAsync(ct);

        return [.. Enumerable.Range(1, 12).Select(month =>
        {
            // Gelir alacakla artar, gider borçla.
            var revenue = rows
                .Where(r => r.Month == month && r.Type == AccountType.Revenue)
                .Sum(r => r.Credit - r.Debit);

            var expense = rows
                .Where(r => r.Month == month && r.Type == AccountType.Expense)
                .Sum(r => r.Debit - r.Credit);

            return new MonthlyResult(year, month, revenue, expense);
        })];
    }

    // ------------------------------------------------------------------

    private sealed record Aggregate(
        string Code, string Name, AccountType Type, decimal Debit, decimal Credit);

    private async Task<List<Aggregate>> AggregateAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var db = factory.Create();

        return await db.JournalLines.AsNoTracking()
            .Where(l => l.JournalEntry.IsPosted
                     && l.JournalEntry.EntryDate >= from
                     && l.JournalEntry.EntryDate <= to)
            .GroupBy(l => new { l.AccountCode, l.AccountName, l.Account.Type })
            .Select(g => new Aggregate(
                g.Key.AccountCode, g.Key.AccountName, g.Key.Type,
                g.Sum(l => l.Debit), g.Sum(l => l.Credit)))
            .ToListAsync(ct);
    }

    private static StatementGroup Group(
        string title, List<Aggregate> rows, AccountType type, bool debitNormal)
    {
        var lines = rows
            .Where(r => r.Type == type)
            .Select(r => new StatementLine(
                r.Code, r.Name,
                debitNormal ? r.Debit - r.Credit : r.Credit - r.Debit))
            .Where(l => l.Amount != 0m)
            .OrderBy(l => l.Code)
            .ToList();

        return new StatementGroup(title, lines);
    }
}
