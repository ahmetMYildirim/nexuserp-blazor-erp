using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Parties;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Accounting;

/// <summary>
/// Manuel muhasebe fişi: taslak kaydetme, kesinleştirme, listeleme.
///
/// Taslak/kesinleşmiş ayrımı faturadakiyle aynı: taslak serbestçe değişir,
/// kesinleşmiş fiş dokunulmazdır. Fark şu ki fişin kesinleşmesi ayrıca
/// DENGELİ olmasını da şart koşuyor — dengesiz fiş kaydedilebilir (kullanıcı
/// satırları girerken zaten dengesizdir) ama kesinleştirilemez.
/// </summary>
public sealed class JournalService(
    IAppDbContextFactory factory,
    IInvoiceNumberGenerator numbers,
    TimeProvider clock)
{
    private const string Series = "MUH";
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    // ------------------------------------------------------------------ okuma

    public async Task<PagedResult<JournalEntryListItem>> SearchAsync(
        string? search = null, bool? onlyPosted = null,
        int page = 0, int pageSize = 25, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var query = db.JournalEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ⚠️ ILike Npgsql'e özel bir uzantı ve Application katmanı o paketi
            // referans etmiyor (Clean Architecture sınırı). Kod tabanının geri
            // kalanıyla aynı yol: Türkçe kültürle ToUpper + Like.
            var pattern = "%" + search.Trim().ToUpper(Tr) + "%";
            query = query.Where(j =>
                (j.Number != null && EF.Functions.Like(j.Number.ToUpper(), pattern)) ||
                EF.Functions.Like(j.Description.ToUpper(), pattern) ||
                (j.SourceDocumentNumber != null
                 && EF.Functions.Like(j.SourceDocumentNumber.ToUpper(), pattern)));
        }

        if (onlyPosted is not null)
            query = query.Where(j => j.IsPosted == onlyPosted.Value);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(j => j.EntryDate).ThenByDescending(j => j.Number)
            .Skip(page * pageSize).Take(pageSize)
            .Select(j => new JournalEntryListItem(
                j.Id, j.Number, j.EntryDate, j.Description, j.SourceType,
                j.SourceDocumentNumber, j.DebitTotal, j.CreditTotal, j.IsPosted))
            .ToListAsync(ct);

        return new PagedResult<JournalEntryListItem>(items, total);
    }

    public async Task<JournalEntryDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var entry = await db.JournalEntries.AsNoTracking()
            .Include(j => j.Lines.OrderBy(l => l.LineNumber))
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (entry is null) return null;

        return new JournalEntryDetail(
            entry.Id, entry.Number, entry.EntryDate, entry.Description,
            entry.SourceType, entry.SourceText, entry.SourceDocumentNumber,
            entry.IsPosted, entry.DebitTotal, entry.CreditTotal,
            entry.Lines.Select(l => new JournalLineDetail(
                l.LineNumber, l.AccountCode, l.AccountName,
                l.Debit, l.Credit, l.Description)).ToList());
    }

    // ------------------------------------------------------------------ yazma

    public async Task<Guid> SaveDraftAsync(JournalEntryForm form, CancellationToken ct = default)
    {
        if (form.Lines.Count < 2)
            throw new DomainException(
                "Muhasebe fişi en az iki satır içermelidir: her kaydın bir borç " +
                "bir alacak tarafı vardır.");

        if (string.IsNullOrWhiteSpace(form.Description))
            throw new DomainException("Fiş açıklaması zorunludur.");

        await using var db = factory.Create();

        var accountIds = form.Lines.Select(l => l.AccountId).OfType<Guid>().Distinct().ToList();
        var accounts = await db.Accounts
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        JournalEntry entry;
        if (form.Id is null)
        {
            entry = new JournalEntry { Year = form.EntryDate.Year };
            db.JournalEntries.Add(entry);
        }
        else
        {
            entry = await db.JournalEntries.Include(j => j.Lines)
                        .FirstOrDefaultAsync(j => j.Id == form.Id, ct)
                    ?? throw new DomainException("Fiş bulunamadı.");

            entry.EnsureEditable();
            db.JournalLines.RemoveRange(entry.Lines);
            entry.Lines.Clear();
        }

        entry.EntryDate = form.EntryDate;
        entry.Year = form.EntryDate.Year;
        entry.Description = form.Description.Trim();
        entry.SourceType = JournalSourceType.Manual;

        for (var i = 0; i < form.Lines.Count; i++)
        {
            var src = form.Lines[i];

            if (src.AccountId is null)
                throw new DomainException($"{i + 1}. satırda hesap seçilmemiş.");

            if (!accounts.TryGetValue(src.AccountId.Value, out var account))
                throw new DomainException($"{i + 1}. satırdaki hesap bulunamadı.");

            account.EnsurePostable();

            var line = new JournalLine
            {
                LineNumber = i + 1,
                AccountId = account.Id,
                AccountCode = account.Code,
                AccountName = account.Name,
                Debit = src.Debit,
                Credit = src.Credit,
                Description = src.Description
            };

            line.EnsureValid();
            entry.Lines.Add(line);
        }

        entry.RecalculateTotals();
        await db.SaveChangesAsync(ct);

        return entry.Id;
    }

    /// <summary>
    /// Fişi kesinleştirir: numara verir, rapora dahil eder, kilitler.
    /// Dengesizse <see cref="DomainException"/> fırlatır.
    /// </summary>
    public async Task<string> PostAsync(Guid entryId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var entry = await db.JournalEntries.Include(j => j.Lines)
                        .FirstOrDefaultAsync(j => j.Id == entryId, ct)
                    ?? throw new DomainException("Fiş bulunamadı.");

        // ⚠️ Numara SADECE kesinleştirmede veriliyor, taslakta değil: taslak
        // silinebilir ve silinen taslağın numarası seride boşluk bırakır.
        // Dengesizlik kontrolü numara ÜRETİLMEDEN önce yapılıyor — aksi halde
        // reddedilen her deneme bir numara yakardı.
        entry.RecalculateTotals();
        if (!entry.IsBalanced)
            throw new DomainException(
                $"Fiş dengesiz: borç {entry.DebitTotal:N2} — alacak {entry.CreditTotal:N2}, " +
                $"fark {Math.Abs(entry.Difference):N2}. Dengelenmeden kesinleştirilemez.");

        var (number, _) = await numbers.NextAsync(Series, entry.Year, ct);
        entry.Post(number, clock.GetUtcNow());

        await db.SaveChangesAsync(ct);
        return number;
    }

    public async Task DeleteDraftAsync(Guid entryId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var entry = await db.JournalEntries.FirstOrDefaultAsync(j => j.Id == entryId, ct)
                    ?? throw new DomainException("Fiş bulunamadı.");

        if (entry.IsPosted)
            throw new DomainException(
                "Kesinleşmiş fiş silinemez. Düzeltme için ters kayıt fişi girin.");

        db.JournalEntries.Remove(entry);      // soft delete (ADR-009)
        await db.SaveChangesAsync(ct);
    }
}
