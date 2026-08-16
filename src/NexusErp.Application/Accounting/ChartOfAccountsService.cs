using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;

namespace NexusErp.Application.Accounting;

public sealed class ChartOfAccountsService(IAppDbContextFactory factory)
{
    public async Task<IReadOnlyList<AccountListItem>> ListAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var query = db.Accounts.AsNoTracking();

        if (!includeInactive) query = query.Where(a => a.IsActive);

        return await query
            .OrderBy(a => a.Code)
            .Select(a => new AccountListItem(
                a.Id, a.Code, a.Name, a.Type, a.Level,
                a.IsPostable, a.IsActive, a.IsSystem))
            .ToListAsync(ct);
    }

    /// <summary>Fiş satırında seçilebilecek hesaplar — yalnızca yaprak ve aktif.</summary>
    public async Task<IReadOnlyList<AccountOption>> PostableAsync(
        CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await db.Accounts.AsNoTracking()
            .Where(a => a.IsActive && a.IsPostable)
            .OrderBy(a => a.Code)
            .Select(a => new AccountOption(a.Id, a.Code, a.Name))
            .ToListAsync(ct);
    }

    public async Task<Guid> CreateAsync(AccountForm form, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(form.Code))
            throw new DomainException("Hesap kodu zorunludur.");
        if (string.IsNullOrWhiteSpace(form.Name))
            throw new DomainException("Hesap adı zorunludur.");

        var code = form.Code.Trim();

        await using var db = factory.Create();

        if (await db.Accounts.AnyAsync(a => a.Code == code, ct))
            throw new DomainException($"{code} kodlu hesap zaten var.");

        // Üst hesap: kodun kendisinden kısa en yakın atası. "120.01" girildiğinde
        // "120" bulunur ve o hesap ARA HESABA dönüşür — artık ona doğrudan
        // hareket yazılamaz, yoksa mizan hem 120'yi hem 120.01'i toplar ve
        // tutar iki kez sayılır.
        var candidates = await db.Accounts
            .Where(a => code.StartsWith(a.Code))
            .OrderByDescending(a => a.Code.Length)
            .ToListAsync(ct);

        var parent = candidates.FirstOrDefault(a => a.Code.Length < code.Length);

        if (parent is not null && parent.IsPostable)
        {
            var hasMovement = await db.JournalLines.AnyAsync(l => l.AccountId == parent.Id, ct);
            if (hasMovement)
                throw new DomainException(
                    $"{parent.DisplayName} hesabına hareket girilmiş; altına alt hesap " +
                    "açılamaz. Önce mevcut hareketleri alt hesaba taşıyın.");

            parent.IsPostable = false;
        }

        var account = new Account
        {
            Code = code,
            Name = form.Name.Trim(),
            Type = parent?.Type ?? form.Type,
            Description = form.Description,
            ParentId = parent?.Id,
            Level = code.Length,
            IsPostable = true,
            IsActive = true
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        return account.Id;
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct)
                      ?? throw new DomainException("Hesap bulunamadı.");

        // Sistem hesabı pasifleştirilirse otomatik fiş üretimi durur ve fatura
        // kesilemez hale gelir — sebebi ekranda görünmediği için teşhisi zordur.
        if (account.IsSystem && !active)
            throw new DomainException(
                $"{account.DisplayName} bir sistem hesabıdır; otomatik fişler bu hesaba " +
                "yazar ve pasifleştirilemez.");

        account.IsActive = active;
        await db.SaveChangesAsync(ct);
    }
}
