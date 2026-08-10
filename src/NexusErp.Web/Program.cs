using System.Globalization;
using MudBlazor.Services;
using NexusErp.Web.Components;

// Türkçe kültür: 1234.56m.ToString("N2") → "1.234,56"
// ⚠️ Kullanıcı girdisi kültüre duyarlı, makine verisi (JSON/API) InvariantCulture olmalı.
var trCulture = new CultureInfo("tr-TR");
CultureInfo.DefaultThreadCurrentCulture = trCulture;
CultureInfo.DefaultThreadCurrentUICulture = trCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

var app = builder.Build();

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
