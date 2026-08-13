using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using NexusErp.Infrastructure.Identity;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Identity;

/// <summary>
/// Kullanıcı yönetimi. Buradaki testlerin çoğu GÜVENLİK testi:
/// tenant sızıntısı, kendi kendini kilitleme ve son yöneticinin düşürülmesi.
///
/// ⚠️ AppUser ITenantScoped DEĞİL — global query filter ona uygulanmıyor.
/// Servisteki elle TenantId filtresi tek savunma hattı; testi olmayan bir
/// savunma hattı yoktur sayılır.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class UserAdminServiceTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private ServiceProvider _provider = default!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Token üreticileri şifrelenmiş token üretiyor; ASP.NET Core barındırmasında
        // hazır gelen bu servis test konteynerinde elle eklenmeli.
        services.AddDataProtection();

        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention());

        services.AddScoped<ITenantContext, MutableTenant>();
        services.AddScoped<ICurrentUser, MutableUser>();

        services.AddIdentityCore<AppUser>(opt =>
                {
                    opt.Password.RequiredLength = 8;
                    opt.Password.RequireNonAlphanumeric = true;
                    opt.User.RequireUniqueEmail = true;
                })
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AppDbContext>()
                // ⚠️ Parola sıfırlama token üreticisi ister. Uygulamada
                // AddDefaultTokenProviders() zaten kayıtlı; test konteyneri
                // onu taklit etmezse ResetPasswordAsync burada patlar.
                .AddDefaultTokenProviders();

        services.AddScoped<UserAdminService>();

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var role in AppRoles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.CreateVersion7() });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    // ------------------------------------------------------------------

    private sealed class MutableTenant : ITenantContext
    {
        public Guid TenantId { get; private set; }
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class MutableUser : ICurrentUser
    {
        public string UserName { get; set; } = "admin@test.local";
    }

    /// <summary>Belirli bir tenant ve "oturum açmış kullanıcı" için servis kurar.</summary>
    private async Task<(UserAdminService Service, AsyncServiceScope Scope)> ServiceAsync(
        Guid tenant, string currentUser = "admin@test.local")
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant);
        ((MutableUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>()).UserName = currentUser;

        await Task.CompletedTask;
        return (scope.ServiceProvider.GetRequiredService<UserAdminService>(), scope);
    }

    private async Task<UserAdminService> SeedAdminAsync(Guid tenant, string email)
    {
        var (service, _) = await ServiceAsync(tenant, email);

        await using var scope = _provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var admin = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = "İlk Yönetici",
            TenantId = tenant
        };

        (await users.CreateAsync(admin, "Test!2026x")).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(admin, AppRoles.Admin)).Succeeded.ShouldBeTrue();

        return service;
    }

    private static string Mail(Guid tenant, string prefix) =>
        $"{prefix}.{tenant:N}@test.local";

    // ------------------------------------------------------------------

    [Fact]
    public async Task Olusturulan_kullanici_uretilen_parolayla_giris_yapabilir()
    {
        var tenant = Guid.CreateVersion7();
        var email = Mail(tenant, "yeni");
        var service = await SeedAdminAsync(tenant, Mail(tenant, "admin"));

        var password = await service.CreateAsync(
            new CreateUserRequest(email, "Yeni Kullanıcı", AppRoles.Muhasebe));

        password.Length.ShouldBe(12);

        await using var scope = _provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var created = await users.FindByEmailAsync(email);

        created.ShouldNotBeNull();
        created.TenantId.ShouldBe(tenant);
        (await users.CheckPasswordAsync(created, password)).ShouldBeTrue();
        (await users.IsInRoleAsync(created, AppRoles.Muhasebe)).ShouldBeTrue();
    }

    [Fact]
    public async Task Baska_firmanin_kullanicilari_listelenmez()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        var serviceA = await SeedAdminAsync(tenantA, Mail(tenantA, "admin"));
        var serviceB = await SeedAdminAsync(tenantB, Mail(tenantB, "admin"));

        await serviceA.CreateAsync(
            new CreateUserRequest(Mail(tenantA, "a1"), "A Firma Muhasebe", AppRoles.Muhasebe));
        await serviceB.CreateAsync(
            new CreateUserRequest(Mail(tenantB, "b1"), "B Firma Muhasebe", AppRoles.Muhasebe));

        var listA = await serviceA.ListAsync();

        // ⚠️ Bir tek yerde tenant filtresi unutulursa burası patlar.
        listA.Count.ShouldBe(2);
        listA.ShouldAllBe(u => u.Email.Contains(tenantA.ToString("N")));
    }

    [Fact]
    public async Task Baska_firmanin_kullanicisinin_parolasi_sifirlanamaz()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        var serviceA = await SeedAdminAsync(tenantA, Mail(tenantA, "admin"));
        var serviceB = await SeedAdminAsync(tenantB, Mail(tenantB, "admin"));

        await serviceB.CreateAsync(
            new CreateUserRequest(Mail(tenantB, "kurban"), "B Firma Kullanıcı", AppRoles.Satis));

        var victim = (await serviceB.ListAsync()).First(u => u.FullName == "B Firma Kullanıcı");

        // A firmasının yöneticisi B firmasının kullanıcısını GÖREMEZ, dokunamaz.
        await Should.ThrowAsync<DomainException>(() => serviceA.ResetPasswordAsync(victim.Id));
        await Should.ThrowAsync<DomainException>(
            () => serviceA.ChangeRoleAsync(victim.Id, AppRoles.Admin));
        await Should.ThrowAsync<DomainException>(() => serviceA.SetActiveAsync(victim.Id, false));
    }

    [Fact]
    public async Task Yonetici_kendi_hesabini_pasiflestiremez()
    {
        var tenant = Guid.CreateVersion7();
        var adminEmail = Mail(tenant, "admin");
        var service = await SeedAdminAsync(tenant, adminEmail);

        var me = (await service.ListAsync()).Single(u => u.Email == adminEmail);

        var ex = await Should.ThrowAsync<DomainException>(
            () => service.SetActiveAsync(me.Id, false));

        ex.Message.ShouldContain("Kendi hesabınızı");
    }

    [Fact]
    public async Task Yonetici_kendi_rolunu_dusuremez()
    {
        var tenant = Guid.CreateVersion7();
        var adminEmail = Mail(tenant, "admin");
        var service = await SeedAdminAsync(tenant, adminEmail);

        var me = (await service.ListAsync()).Single(u => u.Email == adminEmail);

        var ex = await Should.ThrowAsync<DomainException>(
            () => service.ChangeRoleAsync(me.Id, AppRoles.Goruntuleyici));

        ex.Message.ShouldContain("Kendi rolünüzü");
    }

    [Fact]
    public async Task Son_yonetici_rolden_dusurulemez()
    {
        var tenant = Guid.CreateVersion7();
        var service = await SeedAdminAsync(tenant, Mail(tenant, "admin"));

        // İkinci bir yönetici aç, sonra ilkini düşürmeye çalış — bu SERBEST.
        await service.CreateAsync(
            new CreateUserRequest(Mail(tenant, "admin2"), "İkinci Yönetici", AppRoles.Admin));

        var second = (await service.ListAsync()).Single(u => u.FullName == "İkinci Yönetici");
        await service.ChangeRoleAsync(second.Id, AppRoles.Muhasebe);   // sorun yok

        // Şimdi tek yönetici kaldı; onu başka bir oturumdan düşürmek YASAK.
        var (other, scope) = await ServiceAsync(tenant, "baskasi@test.local");
        await using var _ = scope;

        var lastAdmin = (await other.ListAsync()).Single(u => u.FullName == "İlk Yönetici");

        var ex = await Should.ThrowAsync<DomainException>(
            () => other.ChangeRoleAsync(lastAdmin.Id, AppRoles.Satis));

        ex.Message.ShouldContain("son yönetici");
    }

    [Fact]
    public async Task Son_yonetici_pasiflestirilemez()
    {
        var tenant = Guid.CreateVersion7();
        await SeedAdminAsync(tenant, Mail(tenant, "admin"));

        var (other, scope) = await ServiceAsync(tenant, "baskasi@test.local");
        await using var _ = scope;

        var lastAdmin = (await other.ListAsync()).Single(u => u.FullName == "İlk Yönetici");

        var ex = await Should.ThrowAsync<DomainException>(
            () => other.SetActiveAsync(lastAdmin.Id, false));

        ex.Message.ShouldContain("son yönetici");
    }

    [Fact]
    public async Task Ayni_eposta_ikinci_kez_kullanilamaz()
    {
        var tenant = Guid.CreateVersion7();
        var service = await SeedAdminAsync(tenant, Mail(tenant, "admin"));
        var email = Mail(tenant, "tekrar");

        await service.CreateAsync(new CreateUserRequest(email, "İlk", AppRoles.Satis));

        var ex = await Should.ThrowAsync<DomainException>(
            () => service.CreateAsync(new CreateUserRequest(email, "İkinci", AppRoles.Satis)));

        ex.Message.ShouldContain("zaten");
    }

    [Fact]
    public async Task Parola_sifirlama_eski_parolayi_gecersiz_kilar()
    {
        var tenant = Guid.CreateVersion7();
        var service = await SeedAdminAsync(tenant, Mail(tenant, "admin"));
        var email = Mail(tenant, "sifirla");

        var first = await service.CreateAsync(
            new CreateUserRequest(email, "Sıfırlanacak", AppRoles.Satis));

        var target = (await service.ListAsync()).Single(u => u.Email == email);
        var second = await service.ResetPasswordAsync(target.Id);

        second.ShouldNotBe(first);

        await using var scope = _provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email);

        (await users.CheckPasswordAsync(user!, first)).ShouldBeFalse();
        (await users.CheckPasswordAsync(user!, second)).ShouldBeTrue();
    }

    [Fact]
    public async Task Uretilen_parola_politikayi_saglar()
    {
        var tenant = Guid.CreateVersion7();
        var service = await SeedAdminAsync(tenant, Mail(tenant, "admin"));

        // 20 üretimde de politikaya uymalı — rastgelelik bazen tek karakter
        // sınıfını atlarsa CreateAsync hata verirdi.
        for (var i = 0; i < 20; i++)
        {
            var password = await service.CreateAsync(new CreateUserRequest(
                Mail(tenant, $"kullanici{i}"), $"Kullanıcı {i}", AppRoles.Goruntuleyici));

            password.Length.ShouldBe(12);
            password.ShouldContain(c => char.IsUpper(c));
            password.ShouldContain(c => char.IsLower(c));
            password.ShouldContain(c => char.IsDigit(c));
            password.ShouldContain(c => !char.IsLetterOrDigit(c));
        }
    }

    [Fact]
    public async Task Gecersiz_rol_reddedilir()
    {
        var tenant = Guid.CreateVersion7();
        var service = await SeedAdminAsync(tenant, Mail(tenant, "admin"));

        await Should.ThrowAsync<DomainException>(() => service.CreateAsync(
            new CreateUserRequest(Mail(tenant, "kotu"), "Kötü Rol", "SuperAdmin")));
    }

    [Fact]
    public async Task Pasiflestirilen_kullanici_listede_pasif_gorunur()
    {
        var tenant = Guid.CreateVersion7();
        var service = await SeedAdminAsync(tenant, Mail(tenant, "admin"));
        var email = Mail(tenant, "pasif");

        await service.CreateAsync(new CreateUserRequest(email, "Pasif Olacak", AppRoles.Satis));

        var target = (await service.ListAsync()).Single(u => u.Email == email);
        await service.SetActiveAsync(target.Id, false);

        (await service.ListAsync()).Single(u => u.Email == email).IsActive.ShouldBeFalse();
    }
}
