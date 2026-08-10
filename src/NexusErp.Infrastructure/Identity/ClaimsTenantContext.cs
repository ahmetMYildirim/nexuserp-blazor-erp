using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NexusErp.Application.Abstractions;
using NexusErp.Infrastructure.Tenancy;

namespace NexusErp.Infrastructure.Identity;

public static class AppClaims
{
    public const string TenantId = "tenant_id";
    public const string FullName = "full_name";
}

/// <summary>
/// Tenant'ı oturum açan kullanıcının claim'inden okur.
///
/// ⚠️ Kimlik doğrulanmamışsa yapılandırmadaki varsayılana düşer. Bu ŞART:
/// uygulama açılışında migration/seed işleri HTTP isteği ve oturum olmadan çalışır;
/// istisna fırlatsaydı uygulama hiç ayağa kalkmazdı.
/// </summary>
public sealed class ClaimsTenantContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<TenantOptions> options) : ITenantContext
{
    private Guid? _override;

    public Guid TenantId
    {
        get
        {
            if (_override is { } id) return id;

            var claim = httpContextAccessor.HttpContext?.User?
                .FindFirst(AppClaims.TenantId)?.Value;

            return Guid.TryParse(claim, out var tenantId) && tenantId != Guid.Empty
                ? tenantId
                : options.Value.DefaultTenantId;
        }
    }

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new InvalidOperationException("Tenant kimliği boş olamaz.");
        _override = tenantId;
    }
}

/// <summary>created_by / updated_by kolonlarını oturum açan kullanıcıdan doldurur.</summary>
public sealed class ClaimsCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public string UserName =>
        httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "system";
}

/// <summary>
/// Giriş anında tenant ve ad bilgisini cookie'ye ekler.
///
/// ⚠️ İKİ generic parametreli taban sınıf kullanılmak ZORUNDA.
/// UserClaimsPrincipalFactory&lt;AppUser&gt; (tek generic) ROL CLAIM'İ EKLEMEZ —
/// sayfa [Authorize] ile korunur ama [Authorize(Roles = "...")] ve
/// &lt;AuthorizeView Roles="..."&gt; hiçbir zaman eşleşmez. Sessiz ve kafa karıştırıcı bir hata.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(userManager, roleManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaims.TenantId, user.TenantId.ToString()));
        identity.AddClaim(new Claim(AppClaims.FullName, user.FullName));
        return identity;
    }
}
