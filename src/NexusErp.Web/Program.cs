using System.Globalization;
using MudBlazor.Services;
using NexusErp.Application;
using NexusErp.Application.Accounting;
using NexusErp.Application.Messaging;
using NexusErp.Infrastructure;
using NexusErp.Infrastructure.BackgroundJobs;
using NexusErp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using NexusErp.Infrastructure.Identity;
using NexusErp.Web;
using NexusErp.Web.Components;

// Türkçe kültür: 1234.56m.ToString("N2") → "1.234,56"
// ⚠️ Kullanıcı girdisi kültüre duyarlı, makine verisi (JSON/API) InvariantCulture olmalı.
var trCulture = new CultureInfo("tr-TR");
CultureInfo.DefaultThreadCurrentCulture = trCulture;
CultureInfo.DefaultThreadCurrentUICulture = trCulture;

// QuestPDF Community lisansı: yıllık geliri 1M USD altındaki kullanım ücretsiz
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

// ⚠️ Abonelik faturalandırma YALNIZCA burada kayıtlı, API'de değil.
// İki uygulama birden ayaktayken ikisi de kaydetseydi aynı dönem iki kez
// faturalanmaya çalışılırdı; (subscription_id, period_start) unique index'i
// bunu engeller ama boşuna iş yapılır. Tek sahibi olsun.
builder.Services.AddHostedService<SubscriptionBillingWorker>();

// Outbox yayıncısı ve tüketici.
// ⚠️ Abonelik işçisinin aksine outbox işçisi birden fazla instance'ta güvenle
// çalışır — FOR UPDATE SKIP LOCKED aynı satırı iki kez vermez.
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<InvoiceIssuedConsumer>();
builder.Services.AddHostedService<OutboxCleanupWorker>();
builder.Services.AddHostedService<DunningWorker>();
builder.Services.AddHostedService<NotificationConsumer>();

var app = builder.Build();

// Migration + tohum verisi (geliştirme ortamı)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseSeeder.SeedAsync(db);

    await IdentitySeeder.SeedAsync(
        scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
        scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>());

    // Demo fatura/tahsilat verisi — gerçek servisler üzerinden üretilir
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

    // ⚠️ Otomatik muhasebe fişi bu sürümle geldi; ondan ÖNCE kesilmiş faturaların
    // ve tahsilatların fişi yok. Geri doldurulmazsa mizan ve bilanço yalnızca yeni
    // hareketleri gösterir ve kullanıcı raporları "bozuk" sanır. Idempotent.
    await scope.ServiceProvider.GetRequiredService<AccountingBackfillService>().RunAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAccountEndpoints();

// Sağlık ucu — konteyner orkestratörü ve izleme için.
// ⚠️ Asıl metrik bekleyen SAYISI değil, en eski bekleyen mesajın YAŞI.
app.MapGet("/saglik", async (OutboxHealthService health, CancellationToken ct) =>
{
    var h = await health.CheckAsync(ct);
    return h.IsHealthy
        ? Results.Ok(new { durum = h.Status, ozet = h.Summary, bekleyen = h.Pending, hatali = h.Failed })
        : Results.Json(new { durum = h.Status, ozet = h.Summary, bekleyen = h.Pending, hatali = h.Failed },
                       statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
