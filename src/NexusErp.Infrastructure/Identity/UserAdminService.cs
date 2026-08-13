using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;

namespace NexusErp.Infrastructure.Identity;

public sealed record AppUserListItem(
    Guid Id, string Email, string FullName, IReadOnlyList<string> Roles,
    bool IsActive, DateTimeOffset? LastLoginAt, bool IsLockedOut)
{
    public string RolesText => Roles.Count == 0 ? "—" : string.Join(", ", Roles);

    public string LastLoginText => LastLoginAt is null
        ? "hiç giriş yapmadı"
        : LastLoginAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

public sealed record CreateUserRequest(
    string Email, string FullName, string Role, string? Password = null);

/// <summary>
/// Kullanıcı yönetimi.
///
/// ⚠️ EN ÖNEMLİ NOKTA: AppUser <see cref="ITenantScoped"/> DEĞİL — Identity'nin
/// kendi tablosunda yaşıyor ve global query filter ONA UYGULANMIYOR. Buradaki her
/// sorguya TenantId elle eklenmek zorunda. Bir tek yerde unutulursa bir firmanın
/// yöneticisi başka firmanın kullanıcılarını listeler, rolünü değiştirir,
/// parolasını sıfırlar. Projedeki en tehlikeli sessiz hata sınıfı budur.
/// </summary>
public sealed class UserAdminService(
    UserManager<AppUser> userManager,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<AppUserListItem>> ListAsync(CancellationToken ct = default)
    {
        var users = await userManager.Users
            .Where(u => u.TenantId == tenant.TenantId)      // ⚠️ elle tenant filtresi
            .OrderBy(u => u.FullName)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var items = new List<AppUserListItem>(users.Count);

        foreach (var user in users)
        {
            var roles = await userManager.GetRolesAsync(user);
            items.Add(new AppUserListItem(
                user.Id, user.Email!, user.FullName, [.. roles], user.IsActive,
                user.LastLoginAt,
                user.LockoutEnd is not null && user.LockoutEnd > now));
        }

        return items;
    }

    /// <summary>
    /// Kullanıcı oluşturur ve ilk parolayı döndürür.
    ///
    /// ⚠️ Parola YALNIZCA burada, bir kez düz metin olarak görülür; hiçbir yere
    /// kaydedilmez. Yöneticinin kullanıcıya iletmesi gerekir. Veri tabanında
    /// yalnızca Identity'nin ürettiği hash durur.
    /// </summary>
    public async Task<string> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email))
            throw new DomainException("E-posta zorunludur.");
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new DomainException("Ad soyad zorunludur.");
        if (!AppRoles.All.Contains(request.Role))
            throw new DomainException($"Geçersiz rol: {request.Role}");

        if (await userManager.FindByEmailAsync(email) is not null)
            throw new DomainException($"'{email}' adresiyle bir kullanıcı zaten var.");

        var password = string.IsNullOrWhiteSpace(request.Password)
            ? GeneratePassword()
            : request.Password;

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = request.FullName.Trim(),
            // ⚠️ Yeni kullanıcı DAİMA işlemi yapanın firmasına açılır. TenantId'yi
            // dışarıdan parametre olarak alsaydık, bu ucu çağırabilen herkes
            // istediği firmaya kullanıcı ekleyebilirdi.
            TenantId = tenant.TenantId
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded) throw Fail(created);

        var roled = await userManager.AddToRoleAsync(user, request.Role);
        if (!roled.Succeeded)
        {
            // Rolsüz kullanıcı hiçbir şey yapamaz ama giriş yapabilir — yarım
            // kalmış kaydı bırakmaktansa geri alalım.
            await userManager.DeleteAsync(user);
            throw Fail(roled);
        }

        return password;
    }

    public async Task ChangeRoleAsync(Guid userId, string newRole, CancellationToken ct = default)
    {
        if (!AppRoles.All.Contains(newRole))
            throw new DomainException($"Geçersiz rol: {newRole}");

        var user = await FindInTenantAsync(userId, ct);
        var roles = await userManager.GetRolesAsync(user);

        if (roles.Contains(newRole) && roles.Count == 1) return;   // değişiklik yok

        // ⚠️ Kendi yöneticiliğini bırakan admin ekrandan kilitlenir; geri almak için
        // veri tabanına elle müdahale gerekir.
        if (IsSelf(user) && newRole != AppRoles.Admin)
            throw new DomainException(
                "Kendi rolünüzü düşüremezsiniz. Başka bir yönetici bu değişikliği yapmalı.");

        if (roles.Contains(AppRoles.Admin) && newRole != AppRoles.Admin)
            await EnsureNotLastAdminAsync(user, ct);

        var removed = await userManager.RemoveFromRolesAsync(user, roles);
        if (!removed.Succeeded) throw Fail(removed);

        var added = await userManager.AddToRoleAsync(user, newRole);
        if (!added.Succeeded) throw Fail(added);
    }

    /// <summary>
    /// Kullanıcıyı pasifleştirir/aktifleştirir.
    ///
    /// ⚠️ SİLMİYORUZ: created_by/updated_by ve denetim kayıtları bu kullanıcıya
    /// atıfta bulunuyor. Silinen kullanıcı geçmişi okunamaz hale getirir.
    /// </summary>
    public async Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        var user = await FindInTenantAsync(userId, ct);

        if (user.IsActive == active) return;

        if (!active)
        {
            if (IsSelf(user))
                throw new DomainException("Kendi hesabınızı pasifleştiremezsiniz.");

            if (await userManager.IsInRoleAsync(user, AppRoles.Admin))
                await EnsureNotLastAdminAsync(user, ct);
        }

        user.IsActive = active;

        // ⚠️ IsActive tek başına YETMEZ: zaten açık bir oturumu olan kullanıcı
        // çerezi geçerli olduğu sürece çalışmaya devam eder. Damgayı değiştirmek
        // Identity'nin oturum doğrulamasında o çerezi geçersiz kılar.
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded) throw Fail(result);

        await userManager.UpdateSecurityStampAsync(user);
    }

    /// <summary>Yeni geçici parola üretir ve döndürür. Düz metin saklanmaz.</summary>
    public async Task<string> ResetPasswordAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await FindInTenantAsync(userId, ct);

        var password = GeneratePassword();
        var token = await userManager.GeneratePasswordResetTokenAsync(user);

        var result = await userManager.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded) throw Fail(result);

        return password;
    }

    /// <summary>Art arda hatalı girişten kilitlenen hesabı açar.</summary>
    public async Task UnlockAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await FindInTenantAsync(userId, ct);

        var result = await userManager.SetLockoutEndDateAsync(user, null);
        if (!result.Succeeded) throw Fail(result);

        await userManager.ResetAccessFailedCountAsync(user);
    }

    // ------------------------------------------------------------------

    private async Task<AppUser> FindInTenantAsync(Guid userId, CancellationToken ct)
        => await userManager.Users
               .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenant.TenantId, ct)
           ?? throw new DomainException("Kullanıcı bulunamadı.");

    private bool IsSelf(AppUser user)
        => string.Equals(user.Email, currentUser.UserName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ⚠️ Son yöneticiyi düşürmek firmayı kullanıcı yönetiminden tamamen dışarı
    /// atar: kimse rol veremez, kimse kullanıcı açamaz. Geri dönüşü elle SQL.
    /// </summary>
    private async Task EnsureNotLastAdminAsync(AppUser user, CancellationToken ct)
    {
        var admins = await userManager.GetUsersInRoleAsync(AppRoles.Admin);

        var otherActiveAdmins = admins.Count(a =>
            a.TenantId == tenant.TenantId && a.Id != user.Id && a.IsActive);

        if (otherActiveAdmins == 0)
            throw new DomainException(
                "Firmadaki son yönetici. Önce başka bir kullanıcıya yönetici rolü verin.");
    }

    /// <summary>
    /// Parola politikasını (8+ karakter, harf/rakam/simge) garanti eden üretici.
    /// RandomNumberGenerator kullanılıyor — Random sınıfı tahmin edilebilir ve
    /// parola üretmek için uygun değil.
    /// </summary>
    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";     // I ve O yok: 1/0 ile karışıyor
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";                    // 0 ve 1 yok
        const string symbols = "!?*-+";

        var chars = new List<char>
        {
            Pick(upper), Pick(lower), Pick(digits), Pick(symbols)
        };

        const string all = upper + lower + digits + symbols;
        while (chars.Count < 12) chars.Add(Pick(all));

        // Zorunlu karakterler hep aynı sırada olmasın
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string([.. chars]);

        static char Pick(string source) => source[RandomNumberGenerator.GetInt32(source.Length)];
    }

    private static DomainException Fail(IdentityResult result)
        => new(string.Join(" ", result.Errors.Select(e => Translate(e.Code, e.Description))));

    /// <summary>Identity hataları İngilizce gelir; sık görülenleri Türkçeleştiriyoruz.</summary>
    private static string Translate(string code, string fallback) => code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "Bu e-posta adresi zaten kayıtlı.",
        "PasswordTooShort" => "Parola en az 8 karakter olmalıdır.",
        "PasswordRequiresNonAlphanumeric" => "Parola en az bir simge içermelidir.",
        "PasswordRequiresDigit" => "Parola en az bir rakam içermelidir.",
        "PasswordRequiresUpper" => "Parola en az bir büyük harf içermelidir.",
        "InvalidEmail" => "Geçersiz e-posta adresi.",
        _ => fallback
    };
}
