using Microsoft.AspNetCore.Identity;

namespace NexusErp.Infrastructure.Identity;

/// <summary>
/// Uygulama kullanıcısı.
///
/// ⚠️ Bu sınıf DOMAIN'de değil, Infrastructure'da. Kimlik doğrulama bir altyapı
/// meselesidir; domain "kullanıcı" kavramını yalnızca CreatedBy/UpdatedBy metni olarak
/// bilir. Domain'e koysaydık Microsoft.AspNetCore.Identity paketini oraya sokmak
/// gerekirdi ve ADR-001 ("Domain'in NuGet bağımlılığı yok") ihlal edilirdi.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    /// <summary>Kullanıcının bağlı olduğu firma. TÜM veri erişimi buradan türer.</summary>
    public Guid TenantId { get; set; }

    public string FullName { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>Rol adları — sihirli metin kullanmamak için.</summary>
public static class AppRoles
{
    public const string Admin = "Admin";              // her şey + kullanıcı yönetimi
    public const string Muhasebe = "Muhasebe";        // fatura kesme, tahsilat, rapor
    public const string Satis = "Satis";              // cari + fatura oluşturma, tahsilat YOK
    public const string Goruntuleyici = "Goruntuleyici"; // salt okuma

    public static readonly string[] All = [Admin, Muhasebe, Satis, Goruntuleyici];

    /// <summary>Fatura kesebilen roller.</summary>
    public const string CanIssueInvoice = $"{Admin},{Muhasebe}";

    /// <summary>Tahsilat işleyebilen roller.</summary>
    public const string CanManagePayments = $"{Admin},{Muhasebe}";

    /// <summary>Cari/fatura oluşturabilen roller.</summary>
    public const string CanEdit = $"{Admin},{Muhasebe},{Satis}";
}
