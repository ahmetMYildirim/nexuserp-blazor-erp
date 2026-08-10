using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using NexusErp.Infrastructure.Identity;

namespace NexusErp.Web;

/// <summary>
/// Giriş/çıkış uçları.
///
/// ⚠️ Neden Blazor bileşeni değil? Cookie yazmak HttpContext gerektiriyor;
/// interaktif Blazor devresinde HttpContext yok (SignalR üzerinden çalışıyor).
/// Bu yüzden giriş formu normal bir HTTP POST olarak bu uca gidiyor.
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/hesap/giris", async (
            HttpContext http,
            IAntiforgery antiforgery,
            SignInManager<AppUser> signInManager,
            UserManager<AppUser> userManager) =>
        {
            await antiforgery.ValidateRequestAsync(http);

            var form = await http.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var user = await userManager.FindByEmailAsync(email);
            if (user is null || !user.IsActive)
                return Results.Redirect("/giris?error=" + Uri.EscapeDataString(
                    "E-posta veya parola hatalı."));

            var result = await signInManager.PasswordSignInAsync(
                user, password, isPersistent: true, lockoutOnFailure: true);

            if (result.IsLockedOut)
                return Results.Redirect("/giris?error=" + Uri.EscapeDataString(
                    "Hesap geçici olarak kilitlendi. 15 dakika sonra tekrar deneyin."));

            if (!result.Succeeded)
                return Results.Redirect("/giris?error=" + Uri.EscapeDataString(
                    "E-posta veya parola hatalı."));

            user.LastLoginAt = DateTimeOffset.UtcNow;
            await userManager.UpdateAsync(user);

            // Açık yönlendirme (open redirect) koruması: yalnızca site içi yollar
            var target = !string.IsNullOrWhiteSpace(returnUrl)
                         && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                ? returnUrl
                : "/";

            return Results.Redirect(target);
        });

        app.MapPost("/hesap/cikis", async (
            HttpContext http,
            IAntiforgery antiforgery,
            SignInManager<AppUser> signInManager) =>
        {
            await antiforgery.ValidateRequestAsync(http);
            await signInManager.SignOutAsync();
            return Results.Redirect("/giris");
        });
    }
}
