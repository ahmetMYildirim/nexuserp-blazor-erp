using Microsoft.AspNetCore.Identity;
using NexusErp.Infrastructure.Persistence;

namespace NexusErp.Infrastructure.Identity;

public static class IdentitySeeder
{
    /// <summary>Demo hesapları — README'ye de yazılı, mülakatta parola aranmasın.</summary>
    public const string DemoPassword = "Demo!2026";

    private static readonly (string Email, string FullName, string Role)[] Users =
    [
        ("admin@nexusdemo.com.tr",    "Sistem Yöneticisi", AppRoles.Admin),
        ("muhasebe@nexusdemo.com.tr", "Ayşe Muhasebe",     AppRoles.Muhasebe),
        ("satis@nexusdemo.com.tr",    "Mehmet Satış",      AppRoles.Satis),
        ("bakis@nexusdemo.com.tr",    "Zeynep Görüntüleyici", AppRoles.Goruntuleyici),
    ];

    public static async Task SeedAsync(
        UserManager<AppUser> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        CancellationToken ct = default)
    {
        foreach (var role in AppRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<Guid>(role)
                {
                    Id = Guid.CreateVersion7()
                });

        foreach (var (email, fullName, role) in Users)
        {
            if (await userManager.FindByEmailAsync(email) is not null) continue;

            var user = new AppUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                TenantId = DatabaseSeeder.DemoTenantId
            };

            var result = await userManager.CreateAsync(user, DemoPassword);
            if (result.Succeeded)
                await userManager.AddToRoleAsync(user, role);
        }
    }
}
