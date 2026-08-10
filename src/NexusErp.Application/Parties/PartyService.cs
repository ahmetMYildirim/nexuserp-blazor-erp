using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.ValueObjects;

namespace NexusErp.Application.Parties;

public sealed class PartyService(IAppDbContext db)
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<PagedResult<PartyListItem>> SearchAsync(
        PartyQuery q, CancellationToken ct = default)
    {
        var query = db.Parties.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // Büyük/küçük harf duyarsız arama, sağlayıcıdan bağımsız:
            // upper() veri tabanında çalışır ve DB collation'ı ICU tr-TR olduğu için
            // "istanbul" → "İSTANBUL" doğru dönüşür. C# tarafında da tr-TR ile eşliyoruz.
            // (EF.Functions.ILike de çalışırdı ama Application'a Npgsql bağımlılığı sokardı.)
            var pattern = "%" + q.Search.Trim().ToUpper(Tr) + "%";

            query = query.Where(p =>
                EF.Functions.Like(p.Code.ToUpper(), pattern) ||
                EF.Functions.Like(p.Title.ToUpper(), pattern) ||
                (p.TaxNumber != null && EF.Functions.Like(p.TaxNumber, pattern)));
        }

        if (q.Type is not null)
            query = query.Where(p => (p.Type & q.Type.Value) != 0);

        if (q.IsActive is not null)
            query = query.Where(p => p.IsActive == q.IsActive.Value);

        var total = await query.CountAsync(ct);

        query = (q.SortBy, q.Descending) switch
        {
            (nameof(PartyListItem.Title), false) => query.OrderBy(p => p.Title),
            (nameof(PartyListItem.Title), true) => query.OrderByDescending(p => p.Title),
            (nameof(PartyListItem.City), false) => query.OrderBy(p => p.City),
            (nameof(PartyListItem.City), true) => query.OrderByDescending(p => p.City),
            (nameof(PartyListItem.PaymentTermDays), false) => query.OrderBy(p => p.PaymentTermDays),
            (nameof(PartyListItem.PaymentTermDays), true) => query.OrderByDescending(p => p.PaymentTermDays),
            (_, true) => query.OrderByDescending(p => p.Code),
            _ => query.OrderBy(p => p.Code)
        };

        var items = await query
            .Skip(q.Page * q.PageSize)
            .Take(q.PageSize)
            .Select(p => new PartyListItem(
                p.Id, p.Code, p.Title, p.Type, p.TaxNumber, p.City, p.Phone,
                p.PaymentTermDays, p.IsActive))
            .ToListAsync(ct);

        return new PagedResult<PartyListItem>(items, total);
    }

    public async Task<PartyForm?> GetFormAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Parties.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;

        return new PartyForm
        {
            Id = p.Id,
            Code = p.Code,
            Title = p.Title,
            Type = p.Type,
            TaxNumber = p.TaxNumber,
            TaxOffice = p.TaxOffice,
            ContactName = p.ContactName,
            Email = p.Email,
            Phone = p.Phone,
            Address = p.Address,
            District = p.District,
            City = p.City,
            PaymentTermDays = p.PaymentTermDays,
            CreditLimit = p.CreditLimit,
            Currency = p.Currency,
            IsActive = p.IsActive,
            Notes = p.Notes
        };
    }

    public async Task<Guid> SaveAsync(PartyForm form, CancellationToken ct = default)
    {
        Validate(form);

        var code = form.Code.Trim();

        // DB'deki unique index son savunma hattı; burada kullanıcıya anlamlı mesaj veriyoruz
        var codeTaken = await db.Parties
            .AnyAsync(p => p.Code == code && (form.Id == null || p.Id != form.Id), ct);
        if (codeTaken)
            throw new DomainException($"'{code}' cari kodu zaten kullanılıyor.");

        var isNew = form.Id is null;

        var entity = isNew
            ? new Party()
            : await db.Parties.FirstOrDefaultAsync(p => p.Id == form.Id, ct)
              ?? throw new DomainException("Cari kart bulunamadı.");

        entity.Code = code;
        entity.Title = form.Title.Trim();
        entity.Type = form.Type;
        entity.TaxOffice = form.TaxOffice?.Trim();
        entity.ContactName = form.ContactName?.Trim();
        entity.Email = form.Email?.Trim();
        entity.Phone = form.Phone?.Trim();
        entity.Address = form.Address?.Trim();
        entity.District = form.District?.Trim();
        entity.City = form.City?.Trim();
        entity.PaymentTermDays = form.PaymentTermDays;
        entity.CreditLimit = form.CreditLimit;
        entity.Currency = form.Currency;
        entity.IsActive = form.IsActive;
        entity.Notes = form.Notes?.Trim();

        entity.SetTaxNumber(form.TaxNumber);   // Validate() zaten doğruladı, burada patlamaz

        // Add EN SON: doğrulama hatası olursa context'te öksüz Added entity kalmasın
        if (isNew) db.Parties.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Veri tabanı seviyesinde hata olursa (unique ihlali vb.) takibi temizle,
            // aksi halde kullanıcının bir sonraki denemesi de aynı hatayla patlar.
            if (isNew) db.Detach(entity);
            throw;
        }

        return entity.Id;
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var p = await db.Parties.FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new DomainException("Cari kart bulunamadı.");

        p.IsActive = isActive;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sıradaki cari kodunu önerir: MUS0001, MUS0002...
    /// Kod sabit uzunlukta olduğu için metin sıralaması sayısal sıralamayla aynı.
    /// MUS9999 sonrası bozulur — Faz 2'de sayaç tablosuna geçilecek (Bölüm 08 kalıbı).
    /// </summary>
    public async Task<string> SuggestCodeAsync(PartyType type, CancellationToken ct = default)
    {
        var prefix = type.HasFlag(PartyType.Customer) ? "MUS" : "TED";

        // ⚠️ IgnoreQueryFilters() SOFT DELETE ile birlikte TENANT filtresini de kaldırır.
        // Silinmiş kodları da atlamak istiyoruz ama başka tenant'ın kodlarını GÖRMEMELİYİZ —
        // bu yüzden tenant koşulu elle ekleniyor. (Entegrasyon testi bunu doğruluyor.)
        var last = await db.Parties
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == db.CurrentTenantId && p.Code.StartsWith(prefix))
            .OrderByDescending(p => p.Code)
            .Select(p => p.Code)
            .FirstOrDefaultAsync(ct);

        var next = last is not null && int.TryParse(last[prefix.Length..], out var n) ? n + 1 : 1;
        return $"{prefix}{next:D4}";
    }

    private static void Validate(PartyForm f)
    {
        if (string.IsNullOrWhiteSpace(f.Code))
            throw new DomainException("Cari kodu zorunludur.");
        if (string.IsNullOrWhiteSpace(f.Title))
            throw new DomainException("Ticari unvan zorunludur.");
        if (f.PaymentTermDays is < 0 or > 365)
            throw new DomainException("Ödeme vadesi 0–365 gün aralığında olmalıdır.");
        if (f.CreditLimit < 0)
            throw new DomainException("Kredi limiti negatif olamaz.");
        if (!string.IsNullOrWhiteSpace(f.Email) && !f.Email.Contains('@'))
            throw new DomainException("Geçersiz e-posta adresi.");
        if (f.Type == PartyType.None)
            throw new DomainException("Cari tipi seçilmelidir.");

        // VKN/TCKN'yi BURADA doğruluyoruz (entity'ye dokunmadan önce) — böylece
        // hatalı girişte context'e hiç yazılmıyor.
        if (!string.IsNullOrWhiteSpace(f.TaxNumber) && !TaxIdentifier.TryParse(f.TaxNumber, out _))
            throw new DomainException(
                "VKN 10, TCKN 11 haneli olmalı ve kontrol basamağı doğru olmalı.");
    }
}
