using Microsoft.EntityFrameworkCore;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Seed;

/// <summary>
/// Hesap planını bir tenant için kurar.
///
/// Idempotent: var olan hesaplara dokunmaz, yalnızca eksikleri ekler. Bu
/// sayede hem yeni tenant açılışında hem mevcut veri tabanı üzerinde
/// çalıştırılabiliyor (sistem testi sandbox'ı da bunu kullanıyor).
/// </summary>
public static class ChartOfAccountsSeeder
{
    public static async Task<int> EnsureAsync(
        AppDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var existing = await db.Accounts.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && !a.IsDeleted)
            .ToDictionaryAsync(a => a.Code, ct);

        if (existing.Count >= ChartOfAccountsTemplate.Items.Count) return 0;

        var codes = ChartOfAccountsTemplate.Items.Select(i => i.Code).ToHashSet();
        var added = 0;

        foreach (var item in ChartOfAccountsTemplate.Items)
        {
            if (existing.ContainsKey(item.Code)) continue;

            var account = new Account
            {
                TenantId = tenantId,
                Code = item.Code,
                Name = item.Name,
                Type = item.Type,
                Level = item.Code.Length,
                IsSystem = item.IsSystem,

                // Yaprak mı? Şablonda bu kodla BAŞLAYAN daha uzun bir kod yoksa
                // bu hesap hareket görebilir. Kural koda gömülü değil, hesap
                // planının kendi yapısından türetiliyor: kullanıcı 120'nin altına
                // 120.01 açtığında 120 otomatik olarak ara hesaba dönüşmeli
                // (bkz. ChartOfAccountsService.CreateAsync).
                IsPostable = !codes.Any(c => c.Length > item.Code.Length
                                          && c.StartsWith(item.Code, StringComparison.Ordinal)),

                // Üst hesap: kendisinden bir kısa olan en yakın atası.
                ParentId = FindParentId(item.Code, existing)
            };

            db.Accounts.Add(account);
            existing[item.Code] = account;
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// "120" → "12" → "1" sırasıyla en yakın var olan atayı arar.
    /// Şablon kod uzunluğuna göre sıralı olduğu için ata her zaman önce eklenmiş olur.
    /// </summary>
    private static Guid? FindParentId(string code, Dictionary<string, Account> known)
    {
        for (var len = code.Length - 1; len > 0; len--)
        {
            if (known.TryGetValue(code[..len], out var parent))
                return parent.Id;
        }
        return null;
    }
}
