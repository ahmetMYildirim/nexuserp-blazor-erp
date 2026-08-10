using System.Globalization;
using MudBlazor.Services;
using NexusErp.Application;
using NexusErp.Infrastructure;
using NexusErp.Infrastructure.Persistence;
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

var app = builder.Build();

// Migration + tohum verisi (geliştirme ortamı)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseSeeder.SeedAsync(db);

    // Demo fatura/tahsilat verisi — gerçek servisler üzerinden üretilir
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
